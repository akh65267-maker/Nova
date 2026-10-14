# Messaging

RabbitMQ via MassTransit. All contracts are `sealed record`s in `src/Shared/Contracts`, referenced by both sides — there is no schema registry and no versioning scheme.

## Contracts

| Message | Namespace folder | Shape |
|---|---|---|
| `OrderSubmitted` | IntegrationEvents | `OrderId, UserId, IReadOnlyCollection<OrderLineItem>` |
| `StockReserved` | IntegrationEvents | `OrderId, ProductId` |
| `StockReservationFailed` | IntegrationEvents | `OrderId, ProductId, Reason` |
| `CheckoutTimedOut` | IntegrationEvents | `OrderId` |
| `UserRegistered` | IntegrationEvents | `UserId, Email, RegisteredAtUtc` |
| `ReserveStock` | Commands | `OrderId, ProductId, Quantity` |
| `ReleaseStock` | Commands | `OrderId, ProductId, Quantity` |
| `OrderLineItem` | Common | `ProductId, Quantity, UnitPrice` |

The Commands/IntegrationEvents split is naming only: **every message is sent with `Publish`**, never `Send`. Commands reach their consumer because InventoryService binds explicitly named receive endpoints (`ReserveStock`, `ReleaseStock`) to the published message types.

## Producers and consumers

| Message | Published by | Consumed by |
|---|---|---|
| `UserRegistered` | UserService (`UsersService.RegisterAsync`) | TaskService (`UserRegisteredConsumer`) — out of scope |
| `OrderSubmitted` | OrderService (`OrdersService.CreateAsync`) | OrderService saga (`Initially`) |
| `ReserveStock` | OrderService saga | InventoryService `ReserveStockConsumer` |
| `StockReserved` | InventoryService `ReserveStockConsumer` | OrderService saga |
| `StockReservationFailed` | InventoryService `ReserveStockConsumer` | OrderService saga |
| `ReleaseStock` | OrderService saga (compensation) | InventoryService `ReleaseStockConsumer` |
| `CheckoutTimedOut` | OrderService `CheckoutTimeoutSweeper` (straight to the bus, **not** the outbox) | OrderService saga |

`ReleaseStockConsumer` publishes nothing — compensation is fire-and-forget from the saga's point of view.

## Outbox / inbox

| Service | Configuration | Effect |
|---|---|---|
| UserService | `AddEntityFrameworkOutbox<UserDbContext>` + `UseBusOutbox` | outbox only (no consumers) |
| OrderService | `AddEntityFrameworkOutbox<OrderDbContext>` + `UseBusOutbox`; endpoints via `ConfigureEndpoints`, with `UseEntityFrameworkOutbox` added to them by `AddConfigureEndpointsCallback` | bus outbox for HTTP-originated publishes, and inbox **and** outbox on the saga endpoint (its `ReserveStock`/`ReleaseStock` are held until the saga's transaction commits; redelivered responses are dropped) |
| InventoryService | `AddEntityFrameworkOutbox<InventoryDbContext>` (no `UseBusOutbox`) + `UseEntityFrameworkOutbox` per receive endpoint | inbox **and** outbox on both consumers |

Inbox/outbox tables are created by `AddInboxStateEntity()` / `AddOutboxStateEntity()` / `AddOutboxMessageEntity()` in `OrderDbContext` and `InventoryDbContext`.

**The load-bearing ordering rule, repeated in three places in the code:** `Publish` must be called *before* the `SaveChangesAsync` that commits the business change. The outbox only flushes buffered messages during a `SaveChanges` on the same DbContext; publishing afterwards leaves the message buffered with nothing to flush it, and it is silently lost. Both `ReserveStockConsumer` early-return paths still call `SaveChangesAsync` for the same reason — it is also what writes the inbox dedup entry.

## What the implementation actually guarantees

Verified:

- **At-least-once delivery.** Standard RabbitMQ + MassTransit redelivery; nothing suppresses duplicates at the broker.
- **Atomic "state change + message emitted"** where the outbox is used (UserService register, OrderService create order, both Inventory consumers): one DB transaction covers the row and the outbox record.
- **Consumer-side deduplication**, by `MessageId + ConsumerId` via the EF inbox, in InventoryService's consumers and on the OrderService saga endpoint. It is what prevents a redelivered `ReserveStock` from double-decrementing stock, and a redelivered `StockReserved` from being counted twice. A concurrent duplicate logs an `ERR` `DbUpdateException` (unique constraint `AK_InboxState_MessageId_ConsumerId`) and a `WRN R-RETRY` before being discarded; that is the mechanism working, not a fault.
- **The saga publishes only after its own commit** (outbox on its endpoint), so a reply can never reach it before its row exists. Before this, a fast InventoryService could answer first and the reply was dropped silently.
- **Bounded retry then circuit breaking** on Inventory and Order (intervals and thresholds in [services.md](services.md)).

Not provided by this implementation — do not assume it:

- **No exactly-once guarantee.** Deduplication is by `MessageId`, so it only catches a redelivery of the *same message*; a logically duplicate message published twice with different ids would be processed twice. The inbox retention window also bounds it (MassTransit's cleanup service removes old inbox rows).
- **No distributed transaction across services.** Within OrderService the saga endpoint's outbox puts the saga state, the order write and the outgoing messages on one shared `DbContext` transaction, but whether the order write and the saga write commit atomically has not been tested (see [TODO.md](TODO.md)).
- **No message ordering guarantee.** The saga is written to tolerate arbitrary arrival order of per-item responses, and `UseMessageRetry` on OrderService exists specifically because concurrent responses race on the same saga row.
- **No dead-letter/fault handling of its own.** No `ReceiveEndpoint` fault consumers, no `_error` queue monitoring, no `IConsumeObserver`. Messages that exhaust retries go to MassTransit's default `_error` queue and are not surfaced anywhere.
- **No message scheduler.** No `MassTransit` scheduler is configured. The saga's timeout is driven by a database sweep that publishes `CheckoutTimedOut` instead (see [order-flow.md](order-flow.md)). A timed-out saga is kept in a `TimedOut` state for a retention window so a reservation that completes late can still be released.

## Retries and parked messages

A message that fails is retried in process first. Two retries are stacked on every OrderService and InventoryService endpoint, and the later-registered one is the outer:

| Retry | Handles | Schedule | Why |
|---|---|---|---|
| Short (inner) | every exception | 4 attempts over about 2 s (inventory: about 7 s) | Serialization conflicts (`40001`) between concurrent saga messages resolve in milliseconds |
| Outage (outer, `DatabaseOutage`) | only "the database is not there": refused/dropped connection, timeout, server shutting down, in recovery (`57P03`), out of connections (`53300`) | 18 attempts, 1 s growing to 30 s, about 5.7 min in total | A restart outlasts the short retry; parking the message instead loses the work |

Anything still failing after both is parked in `<queue>_error`. **Nothing consumes those queues.** Look at them with `deploy/scripts/replay-errors.sh --list`; trace one order with `deploy/scripts/diagnose-order.sh <order-id>`.

### Replaying a parked queue

`deploy/scripts/replay-errors.sh <queue>_error` (or `--all`, `--dry-run`) moves every message back to its input queue using the shovel plugin, headers and body intact, acknowledging each only after the destination has it. It replays **everything**, so:
- Do it soon after the outage, with the services healthy. A `ReserveStock` for an order that was cancelled meanwhile takes stock for nothing; whether that gets released depends on the saga still existing (`Checkout:TimedOutRetention`, 24 h by default, 15 minutes in load tests).
- It is safe for `OrderSagaState_error`: the saga starts only for a Pending order, ignores a duplicate for a live checkout, releases a late reservation for a timed-out one, discards events for a saga that is gone, and ends a checkout that conflicts with an already-resolved order.
- A message that fails for a reason other than the outage fails again and comes back; the counts printed afterwards show it.

## Connection configuration

`RabbitMq:Host` / `RabbitMq:Username` / `RabbitMq:Password`, defaulting to `localhost` and the RabbitMQ guest account when unset. OrderService additionally reads `RabbitMq:Port` (default 5672) — the other services hardcode the default port in their `cfg.Host(...)` overload, which is why the integration test can only redirect OrderService to an arbitrary Testcontainers port and must configure Inventory's bus itself.
