# Order flow (checkout saga)

Orchestrated by `OrderSagaStateMachine` in OrderService. One saga instance per order, correlated by `OrderId` on all three events. One `ReserveStock` command per line item; the saga finalizes once it has received one response per item.

## Flow

```text
Client
  │ POST /orders  (Bearer JWT; items incl. the UnitPrice the client was shown)
  ▼
ApiGateway  →  OrderService.OrdersService.CreateAsync
  │
  ├── new Order(userId, items)          → Status = Pending
  ├── check every UnitPrice against CatalogService (GET /products/{id}):
  │     unknown product → 422, price differs → 409, catalog unreachable → 503 — nothing is stored
  ├── Publish(OrderSubmitted)           → buffered in outbox
  └── SaveChangesAsync                  → order row + outbox msg, ONE transaction
  │
  │ 201 Created { id }    ← returned immediately; checkout is still in progress
  ▼
OrderSubmitted  ──▶  Saga (Initially)
        stores UserId, TotalItems, ItemsJson, ResponseCount=0, HasFailure=false
        Publish ReserveStock ×N  →  TransitionTo(AwaitingStockReservation)
  ▼
InventoryService.ReserveStockConsumer      (inbox dedup + outbox, per message)
  ├── no inventory record → StockReservationFailed("No inventory record …")
  ├── item.Reserve(qty) OK → StockReserved            (stock decremented)
  └── InsufficientStockException → StockReservationFailed(ex.Message)
  ▼
Saga (During AwaitingStockReservation)
  ├── StockReserved          → ResponseCount++, append ProductId to ReservedProductIds
  ├── StockReservationFailed → HasFailure=true, FailureReason ??= Reason, ResponseCount++
  └── CheckoutTimedOut       → HasFailure=true, FailureReason ??= "Timed out waiting for
  │                            stock reservation responses."  (published by the sweeper when the
  │                            saga is older than the timeout). Compensates at once, whatever
  │                            ResponseCount is, then moves to TimedOut instead of finalizing
  │
  └── when ResponseCount >= TotalItems  →  FinalizeAsync + Finalize()
        ├── HasFailure = false → order.Confirm()                    → Confirmed
        └── HasFailure = true  → Publish ReleaseStock for every
                                 already-reserved ProductId,
                                 order.Cancel(FailureReason)        → Cancelled
        └── repo.SaveChangesAsync()
  ▼
ReleaseStockConsumer  →  item.Release(qty)   (compensation, stock restored)
```

`SetCompletedWhenFinalized()` means the saga row is deleted once finalized — there is no post-mortem record of a completed checkout beyond the order row and the logs.

## State

`OrderSagaState` (`OrderSagaStates` table, PK `CorrelationId` = `OrderId`, `RowVersion` as EF row-version):

| Field | Purpose |
|---|---|
| `CurrentState` | `Initial` → `AwaitingStockReservation` → finalized (row removed), or → `TimedOut` (row **kept** for `Checkout:TimedOutRetention`, then purged by the sweeper) |
| `CreatedAtUtc` | when the saga started; the timeout deadline is `CreatedAtUtc + Checkout:Timeout`. Existing rows were stamped with the migration time, so a deploy gives each in-flight saga a full window rather than reading as long expired |
| `TotalItems` / `ResponseCount` | completion counter; finalize when `ResponseCount >= TotalItems` |
| `HasFailure` / `FailureReason` | first failure reason wins (`??=`), becomes the order's `CancellationReason` |
| `ItemsJson` | serialized `OrderLineItem[]`, needed to know quantities when compensating |
| `ReservedProductIdsJson` | which products actually got reserved, so only those are released |

`ItemsJson`/`ReservedProductIdsJson` are JSON strings rather than owned EF collections, deliberately, to keep the saga mapping simple.

## Failure and consistency characteristics

- **Compensation is verified by test**, not just by inspection: `Checkout_WithInsufficientStock_CancelsOrderAndReleasesReservedItems` asserts the plentiful product's stock returns to its original value while the scarce one is untouched.
- **Partial reservations are visible to other callers** while the saga is in flight. Reserving decrements `QuantityAvailable` immediately, so between reservation and compensation `GET /inventory` reports the lower number. This is compensating-transaction (eventual) consistency, not isolation.
- **Whether `FinalizeAsync`'s order write and the saga state commit atomically is unverified.** This used to be documented as separate transactions, but that was never tested, and `FinalizeAsync` runs on the same scoped `DbContext` as the saga repository (with the saga endpoint's outbox now sharing it too). A failure-injection test would settle it; until then do not rely on either answer. See [TODO.md](TODO.md).
- **A saga only starts for a Pending order.** `OrderSubmitted` for an order that is already resolved, or unknown, is logged and skipped (replay and duplicates would otherwise reserve its stock a second time). A second `OrderSubmitted` while the checkout is in flight, or after it timed out, is ignored.
- **A checkout whose outcome conflicts with the order's state ends instead of faulting.** If the saga fails or times out but the order is already Confirmed (or succeeds for a Cancelled one), `FinalizeAsync` logs it at error level, leaves the order unchanged and lets the saga finish. It does not release stock. Before this it threw, and because the sweeper republishes the timeout on every tick the saga faulted and parked a message every tick, forever.
- **`Confirm()`/`Cancel()` are idempotent for the same state** and still throw for the opposite one. A finalize that finds the order already in the state it would produce saves nothing, counts nothing and publishes no `ReleaseStock`; it logs a warning and the saga finishes. A confirmed order whose checkout failed (or the reverse) is a conflict and faults the message.
- **Timeout.** `CheckoutTimeoutSweeper` (a `BackgroundService` in OrderService) scans `OrderSagaStates` every `Checkout:SweepInterval` (default 30 s) for sagas older than `Checkout:Timeout` (default 5 min) and publishes `CheckoutTimedOut`. The saga then compensates through the **same path as a rejected reservation**: `ReleaseStock` for every product it had recorded as reserved, and the order becomes `Cancelled` with reason "Timed out waiting for stock reservation responses." Unlike the other two outcomes it then moves to `TimedOut` rather than finalizing, so the row survives (see the late-reservation bullet below).
  - *Why a sweep and not a MassTransit `Schedule`:* no scheduler is configured, and the durable options each cost infrastructure (RabbitMQ's delayed-exchange plugin is not in the stock image; Quartz brings its own schema). The deadline is derived from a persisted column, so a restart resumes enforcing deadlines; an in-memory scheduler would silently drop them.
  - The event is published straight to the bus (not the outbox): the sweep is already a retry loop, since the saga row remains until it is finalized. A `CheckoutTimedOut` that finds no saga (it just finished normally, or a duplicate) is discarded via `OnMissingInstance`; the default would fault it.
  - Verified by `Checkout_WhenStockResponseNeverArrives_TimesOutAndCancelsOrder` (InventoryService stopped) and on the running docker-compose stack with a 30 s override (order cancelled after 31 s).
  - **A reservation that completes after the timeout is released too.** The timeout only compensates what the saga had recorded at that moment, but a `ReserveStock` can still be queued (InventoryService was down) and is consumed later, taking stock for an order that is already cancelled. Finalizing at the timeout used to delete the saga, so that late `StockReserved` found nothing and was dropped silently (observed on the running stack: no fault, no `_error` queue, no log line, stock 23 → 22). Now the saga waits in `TimedOut`; a late `StockReserved` sends `ReleaseStock` for that line and increments `order_late_reservation_released_total`. Duplicates are safe: `ReservedProductIdsJson` records everything already released (by the timeout or by earlier late releases) and a line is released once, so an at-least-once redelivery cannot inflate stock. A late `StockReservationFailed` took no stock and is ignored.
  - **Retention.** `TimedOut` rows are deleted by the sweeper once `Checkout:TimedOutRetention` (default 24 h) has passed since the deadline, measured from `CreatedAtUtc`. A reservation that completes after that has no saga and is lost, silently, as before. Size it to how long InventoryService can be down; the rows are tiny.
  - Verified by `Checkout_WhenReservationCompletesAfterTimeout_ReleasesItAndThenPurgesTheSaga`.
  - **Limit — only sagas that exist are covered.** If `OrderSubmitted` never produces a saga row, nothing cancels that order; `order_pending_oldest_age_seconds` and `OrderStuckPending` are still the only signal.
  - **Limit — granularity and hosting.** An order is cancelled up to one sweep interval after its deadline, and only while OrderService is running (at least one replica). Keep the timeout far above a healthy checkout (well under a second): a slow-but-healthy checkout that outlives it is cancelled.
- **The client is not told the outcome.** `POST /orders` returns `201` immediately; the only way to learn the result is polling `GET /orders/{id}` (which is exactly what the integration test does). There is no callback, webhook, SSE or notification event.
- **The saga's endpoint has the transactional inbox and outbox.** Its `ReserveStock`/`ReleaseStock` are held until the saga's transaction commits, so InventoryService can never answer before the saga row exists (before this, a fast reply was dropped and the checkout hung until the timeout), and a redelivered `StockReserved`/`StockReservationFailed` is discarded by `MessageId` rather than counted again (before this, a two-item order could be confirmed after hearing about one item). Both are covered by integration tests that failed before the change. A concurrent duplicate logs an `ERR` `DbUpdateException` and a `WRN R-RETRY` before it is discarded; that is the dedupe working.
