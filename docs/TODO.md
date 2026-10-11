# Findings: risks, gaps, open questions

Each item is labelled **Confirmed** (verified in code), **Potential risk** (real code path, impact depends on conditions not fully verifiable here), or **Uncertain** (needs a decision or information the repository doesn't contain). Trivial imperfections are omitted.

## Security

**Resolved — order prices are now checked against the catalog.**
`POST /orders` used to store the caller's `UnitPrice` verbatim, so an order could be placed at any price. `OrdersService` now asks CatalogService for the real price of every product (`CatalogClient` → `GET /products/{id}`) and refuses the order, storing and publishing nothing, if a product does not exist (`422`), the price differs (`409`, so a changed price is never charged silently) or the catalog cannot be reached (`503`, no retry, 5 s timeout, so a price is never guessed). Covered by unit tests (including the catalog client against a stub HTTP handler), two saga integration tests that go through the real HTTP pipeline, and verified on the running stack: wrong price → 409, unknown product → 422, catalog stopped → 503, then 201 once it was back.
*Remaining:* (1) **It adds a synchronous dependency**: OrderService cannot take orders while CatalogService is down, where before only browsing was affected. (2) **A stale cart is refused, not repaired**: the frontend shows the server's message but does not refresh the cart's prices. There is no price-edit endpoint today, so a mismatch can only come from tampering or direct database edits; once one exists the cart needs a "prices changed, review" step. (3) The price is read just before the order is saved, not inside its transaction, so a price changed in that instant is accepted at the old price (the order records what the customer was shown, which is the point). (4) `Catalog:BaseUrl` is a new required setting: set in `deploy/docker-compose.yml` and `infra/main.bicep`, not yet exercised on Azure.
`src/Services/OrderService/Application/OrdersService.cs`, `Infrastructure/Catalog/CatalogClient.cs`

**Confirmed — development JWT signing key and admin credentials are committed in `appsettings.json`.**
Every service's `appsettings.json` carries a placeholder `Jwt:Key`; UserService's also carries a placeholder `Admin:Email`/`Admin:Password`. Compose overrides all of them from the git-ignored `deploy/.env`, and the code comments label them dev-only — but they remain live fallbacks for any deployment that forgets to override, and the token key is symmetric (holding it means being able to mint admin tokens). Consider failing startup on the known placeholder value outside Development.

**Confirmed — `GET /users` exposes every user's email to any authenticated caller.**
No `Admin` requirement, no paging, no filtering. `src/Services/UserService/Program.cs`

**Confirmed — inventory levels are publicly readable.**
`GET /inventory` and `GET /inventory/{productId}` have no `RequireAuthorization`, unlike the write endpoints. Whether that is intentional is not stated anywhere. `src/Services/InventoryService/Program.cs`

**Confirmed — no account lockout; login rate limiting is now in place.**
`POST /users/login` is now rate-limited by IP: 10 requests per minute (sliding window, no queue). PBKDF2 at 100k iterations makes each attempt CPU-expensive so the limit is intentionally tight. No account-lockout mechanism exists — repeated failures from different IPs are not correlated.

**Confirmed — tokens cannot be revoked.**
A `jti` claim is minted but never persisted or checked, and there is no refresh token. Every token stays valid for its full hour. `JwtTokenGenerator`

## Distributed-system behaviour

**Resolved — the saga now has a timeout.**
`CheckoutTimeoutSweeper` cancels any checkout still waiting after `Checkout:Timeout` (default 5 min) and releases the stock the saga had recorded as reserved; the next item covers reservations still in flight. Covered by an integration test and verified on the running stack. How it works and its limits: [order-flow.md](order-flow.md). `order_pending_oldest_age_seconds` and the `OrderStuckPending` alert remain as the backstop for anything the sweeper cannot resolve.
`src/Services/OrderService/Infrastructure/CheckoutTimeoutSweeper.cs`, `Application/Sagas/OrderSagaStateMachine.cs`

**Resolved — a stock reservation that completes after the timeout is now released.**
The first version of the timeout finalized the saga, deleting its row, so a `ReserveStock` still queued at that moment (InventoryService down) took stock for a cancelled order and the resulting `StockReserved` was dropped silently. Reproduced on the docker-compose stack before the fix: order `Cancelled`, stock 23 → 22, no fault, no `_error` queue, no log line. The saga now moves to a `TimedOut` state and is kept for `Checkout:TimedOutRetention`, so a late `StockReserved` releases that reservation. Details, and `order_late_reservation_released_total`: [order-flow.md](order-flow.md).
*Residual:* a reservation that completes after the retention window (default 24 h) is still lost, silently. `TimedOutRetention` should exceed the longest InventoryService outage you intend to survive.
`src/Services/OrderService/Application/Sagas/OrderSagaStateMachine.cs`

**Resolved — a fast `StockReserved` could outrun the saga's own commit and be dropped, hanging the checkout.**
The saga published `ReserveStock` while its own transaction was still open and only then inserted and committed its row, so a quick InventoryService could answer first: `StockReserved` looked the saga up, found nothing, and was dropped without a fault. On the running stack this showed up as the first order after `order-api` restarted stalling until the timeout cancelled it, with that order's stock lost (22 → 21). Reproduced deterministically by `Checkout_WhenInventoryRepliesBeforeTheSagaCommits_StillConfirms`, which holds the saga's `INSERT` open with a table lock while InventoryService is healthy: before the fix the order ended `Cancelled`. Fixed by `UseEntityFrameworkOutbox<OrderDbContext>` on the saga endpoint, which holds the saga's publishes until its transaction commits. After: the test confirms the order, and four consecutive first-orders-after-restart on the running stack each confirmed in about a second.

**Resolved — a redelivered stock response was counted twice.**
The saga endpoint had no inbox, so an at-least-once redelivery of `StockReserved` incremented `ResponseCount` again. Reproduced: a two-item order was **confirmed after hearing about only one item**, with the second item never reserved. The same endpoint middleware includes the inbox, which deduplicates by `MessageId` and consumer. `Checkout_WhenAStockResponseIsRedelivered_DoesNotConfirmBeforeEveryItemHasAnswered` failed before (order `Confirmed`) and passes after (`Pending`).
*Log noise, not a failure:* when two copies of one message are processed at the same moment, the loser hits the inbox's unique constraint, so an `ERR` `DbUpdateException` (`23505 … AK_InboxState_MessageId_ConsumerId`) and one `WRN R-RETRY` are logged before the retry discards it. It is intermittent, because copies that arrive apart are caught by a plain lookup and log nothing.
`src/Services/OrderService/Program.cs`

**Resolved — `FinalizeAsync` does commit atomically with the saga (it was unverified).**
This was read from the code and never tested, and a later fix (idempotent Confirm/Cancel, below) rested on it. Two failure-injection tests now make the order write fail (a trigger that raises on every `UPDATE "Orders"`) and check what is left behind: `Finalizing_WhenTheOrderCannotBeSaved_RollsTheSagaBackWithIt` — the response that triggered the finalize is not left counted (`ResponseCount` stays 0), the saga stays in `AwaitingStockReservation` and the order stays `Pending`; and `Finalizing_WhenTheOrderCannotBeSaved_DoesNotSendTheReleases` — the `ReleaseStock` messages the failed checkout would have sent are not sent (the reserved stock stays reserved). So the saga, the order and the outbox share one transaction, as the saga endpoint's outbox is meant to guarantee. Both tests passed on the first run, so they guard the property rather than demonstrate a fix.

**Resolved — re-finalizing a resolved order faulted the message.**
`Order.Confirm()`/`Cancel()` threw `InvalidOperationException` unless the order was `Pending`, so a second `FinalizeAsync` for the same order (a redelivery after the order's own write had committed) faulted the stock response and left the saga stuck in `AwaitingStockReservation` although the order was done. Reproduced by two integration tests that resolve the order behind the saga's back while InventoryService is down (both failed with `StockReserved faulted`). Both methods are now idempotent for the *same* state (they return whether they changed anything; `Cancel` keeps the original reason), and `FinalizeAsync` skips saving, counting and, importantly, **publishing `ReleaseStock` again** when nothing changed, logging a warning instead. The *opposite* state (a confirmed order whose checkout failed, or the reverse) still throws: that is a conflict, not a repeat.
*The assumption behind skipping the releases on a repeat* (that the first finalize's order write and its `ReleaseStock` publishes committed together) is now tested; see the atomicity item above.

**Partly addressed — nothing consumes the `_error` queue automatically.**
No `IConsumeObserver`/fault consumer is registered, so a message that exhausts retry is parked until someone acts. What exists now: its depth is visible (`rabbitmq_detailed_queue_messages_ready{queue=~".*_error"}`, the `RabbitMqErrorQueueNotEmpty` alert), outages are ridden out by a long retry so far fewer messages get there, and `deploy/scripts/replay-errors.sh` moves them back by hand. Still missing: an Alertmanager (so the alert reaches nobody), and any automatic replay.

**Resolved — concurrent reservations of one product mostly failed, and the failures were silent.**
This item used to say the risk of concurrent reads was "not verifiable". The load tests verified it, and it was worse than a lost update. `ReserveStock` is a read-modify-write on one `InventoryItems` row, run in a MassTransit outbox transaction that defaults to SERIALIZABLE, where Postgres rejects the second writer with `40001 could not serialize access due to concurrent update`. Retries (100/500/1000/5000 ms, all on the same schedule) collided again, so roughly one reservation per product won per round and the rest exhausted their retries into `ReserveStock_error`, which nothing reads. Reproduced deterministically: **40 reservations for one product arriving together, 37 failed**, 3 runs out of 3. It showed up in practice as a broker outage (the backlog arrives at once) leaving the next checkouts cancelled, with a circuit breaker then open for 5 minutes.
Fixed in two parts that only work together: the inventory outbox now runs at `READ COMMITTED` (`InventoryOutbox`), so a second writer waits for the lock instead of failing, and every read-modify-write in a consumer takes the row lock first (`GetByProductIdForUpdateAsync`, `SELECT … FOR UPDATE`). **Neither is safe alone:** with READ COMMITTED and no lock, the same tests fail silently, with 38 of 40 decrements lost and stock overselling. After: all 40 applied in about half a second with no `40001`; demand beyond stock sells exactly the stock, never more; reservations and releases racing on one product leave the stock exactly where it started. It also works across several InventoryService replicas, since the lock is in the database.
`src/Services/InventoryService/Infrastructure/Messaging/InventoryOutbox.cs`, `Infrastructure/Persistence/InventoryRepository.cs`

**Resolved — restocking could lose a concurrent reservation.**
`POST /inventory/{id}/restock` read the quantity, added to it and wrote it back with no transaction, so the row lock the consumers take would have been released the instant its `SELECT` finished. A reservation that committed between the restock's read and its write was overwritten: stock went back up by the restock plus whatever that reservation had taken, while the order that reserved it stayed confirmed. Reproduced deterministically by `Restock_WhileAReservationIsInFlight_DoesNotOverwriteIt` (a reservation held open with the row lock, a restock of 5 started meanwhile, then the reservation commits): with 10 in stock and 4 reserved the stock ended at **15** instead of 11. Fixed by running the restock as a locked read-modify-write inside a transaction (`InTransactionAsync` + `GetByProductIdForUpdateAsync`), so it queues behind in-flight reservations and releases like every other writer; the test then passes and the three reservation concurrency tests still do. Not exercised through the HTTP endpoint on the running stack (that needs an admin token); the test drives the same service and repository against a real Postgres.
`src/Services/InventoryService/Application/InventoryItemsService.cs`

**Potential risk — the saga's own transactions are still SERIALIZABLE, and they do conflict.**
The same default applies to OrderService's saga endpoint. It is observed, not hypothetical: the integration suite logs `40001` from `order-service` (on `UPDATE "InboxState"`) in the tests that deliver several stock responses for one order. In those small tests every conflict was retried and succeeded, and a conflict there is narrower than the inventory one (per order, not per product), so it has **not** been shown to fail. But it is the same mechanism that failed 37 of 40 in InventoryService, with a different hot spot, and the saga has not been load-tested for it: a multi-item order whose responses arrive together is the case to try.

*Update (stress run, ~600 iterations/s):* the saga did log 309 `40001` conflicts under overload, all absorbed by retry; every one of the 7,756 orders still reached Confirmed and stock was conserved. So it costs throughput under overload but has not been shown to lose or corrupt anything. Switching the saga endpoint to READ COMMITTED (as done for inventory) is not done and would need its own analysis.

**Mitigated, with a trade-off — the order database ran out of connections under heavy load.**
At about 600 iterations/s the order Postgres hit `max_connections` (100 of 100, with the monitoring connection refused for ~4 minutes) while every HTTP request still succeeded; checkouts stalled (0% confirmed during the step, 64% overall, 3,442 still Pending when the load stopped) and drained later, with all invariants intact. Every service's pool is now capped (`Maximum Pool Size`; order 80 locally): the database stays reachable (peak 82) and the data stays consistent, but sustainable throughput drops (highest step that held 400 → 300 it/s on this machine, single runs). Details and the table: [database.md](database.md). A smaller cap (40) is worse. **Not solved:** capacity is bound by connections held *idle in transaction* (31 at peak), so the lever is why saga/outbox transactions wait while holding one; that was not investigated. The stress test also first appeared to "hold" at 600 because the generator ran out of VUs at its default ceiling; use `MAX_VUS=1200`.

**Mitigated — a database outage no longer parks messages, and what is parked can be replayed.**
Found when the order Postgres crashed and recovered (~45 s) during a stress run: messages that arrived exhausted their ~2 s retry and were parked in `OrderSagaState_error`, which nothing reads. **3,758 were parked, 1,604 orders stayed `Pending` with no saga to time them out, up to 285 units per product vanished, and three `ReserveStock` rows sat undelivered in the outbox.** Four changes, each with a test that failed first:
- **A long retry for outages only** (`DatabaseOutage`, OrderService and InventoryService): about 5.7 minutes, dense at first, applied only to connection/shutdown/recovery errors, never to ordinary failures. `Checkout_WhenTheOrderDatabaseIsDownForSomeSeconds_StillCompletes` stops the order database for 8 s while a stock response arrives. *Registration order matters:* in MassTransit the retry registered last is the outer one, and the first attempt at this did nothing because it was registered the other way round (the message was parked 2.02 s later, exactly the short retry).
- **A way to repair what is already parked:** `deploy/scripts/replay-errors.sh` (shovel plugin, now enabled locally and in the Azure image). Tried on the real stranded data: 3,758 messages moved, Pending orders 1,604 → 0, stuck outbox rows delivered.
- **A saga only starts for a Pending order.** Replay revealed the hazard: an `OrderSubmitted` replayed for an order already Confirmed started a second saga and reserved the stock again (stock 6 → 2 in the test). Now it is skipped, and a duplicate for a live checkout is ignored.
- **A checkout that conflicts with a resolved order ends instead of looping.** Those replayed duplicates left six sagas whose timeout cancelled a Confirmed order, threw, and was republished by the sweeper on every tick: ~12 parked messages a minute for days. `FinalizeAsync` now logs the conflict at error level, leaves the order alone and lets the saga finish (it does not release stock: which checkout owns the reservation is unknowable there).
Verified live: `loadtests/run.sh chaos --fault order-db-down` (database stopped for 60 s under load): 107 of 107 orders confirmed, stock conserved, **no new messages in any `_error` queue**. *Remaining:* an outage longer than ~5.7 min still parks messages, and replaying them is only safe soon afterwards and only for the saga queue (see the warnings in the script); a stale ReserveStock replayed for a long-cancelled order takes stock nothing releases. Nothing alerts on a non-empty `_error` queue, and `deploy/scripts/diagnose-order.sh <id>` is the tool for tracing one stuck order.

**Confirmed — Jaeger's in-memory trace store grows without bound under load.**
It reached roughly 8 GB across the load runs (used VM memory 10.7 GB of 11.8 GB; 1.9 GB after restarting it), which risks taking the whole stack down with it. Restart it between load runs (`docker restart taskflow-jaeger`) or bound it (`MEMORY_MAX_TRACES`); not configured.

**Confirmed (behaviour, not a fault) — after a broker outage, checkouts resume 25–30 s after the broker is back.**
Measured by the `rabbitmq-down` chaos test. A service that lost RabbitMQ reconnects on a growing backoff: after a 45 s outage InventoryService logged failed attempts at 13, 17 and 19 s apart, the last one just after the broker returned but while it was still booting, so the next attempt landed about 25–30 s after the restore. During that gap checkouts placed after the restore wait (up to ~12 s observed, then the saga timeout is 30 s in tests and 5 min by default). No data is lost: every invariant held across the outage, with the outbox holding the messages. A longer outage means a longer backoff, so the gap grows with it; it has not been measured beyond 45 s. The reconnect schedule is MassTransit's default and is not configured anywhere in this repo.

## Found by hand-testing (2026-10-10)

From using the app, not from the code. Status as of writing:

1. **Cart still shows after logout** (bug, frontend). Logout clears the auth store but not the `tm-cart` storage, so the next person on the same browser sees the previous cart. Fix: clear the cart on an explicit logout (not on token expiry, which would silently drop a cart).
2. **"Items remain"** (bug, frontend). Not yet understood: needs a description of when (after logout, after placing an order, after removing an item).
3. **Admin should see orders analytics** (feature, backend + frontend). OrderService only exposes the caller's own orders, so this needs admin-only aggregate endpoints (counts by status, revenue over time, top products) and a page for them.
4. **An admin dashboard** (feature, frontend). Admin now has, or is about to have, products, orders analytics and a dashboard: that is the "admin outgrows one section" trigger for splitting the frontend into a monorepo with a separate admin app (see `taskmanager-web/docs/frontend.md`, Admin Panel). Decide that before building the dashboard.
5. **An order stuck in "Processing"** (bug). *Not reproduced on a fresh build*: orders placed as a new customer and as the admin both confirmed within 1 s. What was likely behind it on the stack in use at the time: images days old (built before the saga fixes above), the replay-created zombie sagas, a circuit breaker held open after an outage. If it happens again, run `deploy/scripts/diagnose-order.sh <order-id>` and keep its output.
6. **Only the admin's orders confirmed** (bug). *Not reproduced*: customer and admin orders behave the same on the current build (same test as item 5). Nothing in the saga depends on the user's role. Needs the diagnose output from a failing order.

## Operational

**Confirmed — no service applies EF migrations at startup.**
There is no `Database.Migrate()`/`EnsureCreated()` anywhere in `src/`. `docker compose up` produces empty databases and services that fail on first query; schema must be applied manually. This is the first thing to hit when bringing the stack up.

**Resolved — the saga integration test now runs in CI.**
`Saga.IntegrationTests` runs in a dedicated `integration-tests` job in `.github/workflows/ci.yml` on every push/PR to main. GitHub-hosted runners have Docker, so Testcontainers works without extra setup.

**Confirmed — Redis has no persistence volume and baskets have no TTL.**
Recreating the container discards all baskets; keys otherwise live forever. Fine if baskets are meant to be disposable, but that isn't stated anywhere.

**Potential risk — only OrderService can be pointed at a non-default RabbitMQ port.**
The other services call the `cfg.Host(host, "/", …)` overload, hardcoding 5672. `RabbitMq:Port` is read in OrderService only.

## Product gaps / consistency

**Confirmed — the basket is disconnected from checkout.**
BasketService neither publishes nor consumes anything, and nothing clears a basket when an order is created. The client must independently read the basket and post the order items. Either an integration event or an explicit client step is missing.

**Confirmed — no product↔inventory lifecycle link.**
Creating a product does not create an inventory record; `ReserveStock` for a product with no inventory row simply fails the order with "No inventory record for this product." Admins must remember to call `POST /inventory` separately.

**Confirmed — no way to create a second admin.**
The startup seeder is the only source of `Admin` users; `POST /users` always creates a `Customer`, and there is no promote/demote endpoint.

**Confirmed — order outcome is only discoverable by polling.**
`POST /orders` returns `201` while checkout is still running; there is no notification, callback or status-stream. Any client needs a polling loop (as the integration test has).

**Uncertain — is CatalogService's MediatR/CQRS style the intended target for the other services?**
Catalog uses MediatR; the other five use plain application-service classes. The repository gives no indication whether this is a migration in progress or a one-off. Worth settling before adding features to either style.

**Uncertain — where is the frontend?**
The task brief describes a Next.js/React Query/Zustand frontend, and the gateway's CORS default (`http://localhost:3100`) implies one exists, but **no frontend code is present in this repository.** It is either a separate repo or not yet written. Nothing about frontend architecture could be verified, so no `frontend.md` was written.
