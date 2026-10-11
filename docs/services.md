# Services

Responsibilities below are verified from `Program.cs`, application services, domain types and consumers — not inferred from names.

| Service | Owns | Store | Bus | Auth on endpoints |
|---|---|---|---|---|
| UserService | Users, credentials, **JWT issuance** | Postgres `userflow` | publisher (outbox) | read = authenticated, register/login = anonymous |
| CatalogService | Products (name, price, category), search/paging | Postgres `catalogflow` | none | read = anonymous, create = `Admin` |
| BasketService | Per-user basket | Redis | none | all endpoints authenticated |
| InventoryService | Stock per product, reserve/release/restock | Postgres `inventoryflow` | consumer + publisher (inbox+outbox) | reads = **anonymous**, writes = `Admin` |
| OrderService | Orders, order status, **checkout saga** | Postgres `orderflow` | publisher + saga (outbox; inbox + outbox on the saga endpoint) | all endpoints authenticated |
| TaskService | out of scope | Postgres `taskflow` | consumes `UserRegistered` | not analysed |

## UserService

Endpoints: `GET /users` (`Admin`), `GET /users/{id}` (your own id, or `Admin`; otherwise 404), `POST /users` (register), `POST /users/login`.

- `UsersService.RegisterAsync`: enforces password ≥ 8 chars, hashes, rejects duplicate email, adds the user, publishes `UserRegistered`, then a **single** `SaveChangesAsync` commits row + outbox message together.
- `LoginAsync`: looks up by email, verifies hash, returns `LoginResult(Token, ExpiresAtUtc)`. Failure throws `InvalidCredentialsException` — the same result whether the email exists or not.
- On startup, seeds one `Admin` user from `Admin:Email` / `Admin:Password` / `Admin:DisplayName` if that email doesn't already exist. This is the **only** way an Admin account comes into existence — there is no promote/invite flow.
- `Pbkdf2PasswordHasher` is registered as a singleton; `IJwtTokenGenerator` and the repository are scoped.

## CatalogService

Endpoints: `GET /products` (filter/sort/page), `GET /products/{id}`, `POST /products` (`Admin`).

- The **only** service using MediatR (`CreateProductCommand`, `GetProductByIdQuery`, `ProductListQuery`). Every other service uses a plain injected `…Service` class. See [decisions.md](decisions.md).
- Query-string parsing lives in `Program.cs`: `sort` ∈ `price-asc | price-desc | name | (default) newest`; `page` defaults to 1; `pageSize` defaults to 20 and is **clamped to ≤ 100**.
- Search uses Postgres `ILIKE` on `Name` only (not description or category).
- The only service using EF `EnableRetryOnFailure` (5 retries, ≤10s) — it has no outbox/saga transaction to conflict with.
- No stock knowledge: `Product` has no quantity. Inventory is keyed by `ProductId` in a different service, with **no enforced referential integrity** between them.

## BasketService

Endpoints (all authenticated, all scoped to the caller's `sub`): `GET /basket`, `POST /basket/items`, `DELETE /basket/items/{productId}`, `DELETE /basket`.

- `Basket` is stored as one JSON blob per user under the Redis key `basket:{userId}`, serialized with web JSON defaults. No EF, no relational store, **no TTL/expiry set**.
- `AddItem` merges by `ProductId` (increases quantity of an existing line); `RemoveItem` removes all lines for a product.
- A missing basket is not an error — `GetAsync`/`AddItemAsync`/`RemoveItemAsync` all fall back to `new Basket(userId)`.
- Redis is a singleton `ConnectionMultiplexer` with `AbortOnConnectFail = false`, so the service starts even if Redis isn't reachable yet and reconnects in the background.
- **The basket is not connected to checkout.** BasketService publishes and consumes nothing, and nothing clears the basket when an order is created — the client must send order items itself.

## InventoryService

Endpoints: `GET /inventory`, `GET /inventory/{productId}` (**no authorization**), `POST /inventory` (`Admin`), `POST /inventory/{productId}/restock` (`Admin`).

- `InventoryItem` is keyed by `ProductId` and holds a single `QuantityAvailable`. Business rules live in the entity: `Reserve` rejects non-positive quantity and throws `InsufficientStockException` when `quantity > QuantityAvailable`; `Release` and `Restock` both add (identical behaviour, kept as separate intents).
- There is **no separate "reserved" counter** — reserving decrements available stock, and compensation adds it back.
- Consumers: `ReserveStockConsumer`, `ReleaseStockConsumer` (see [messaging.md](messaging.md)). Endpoints are declared explicitly (`ReceiveEndpoint("ReserveStock")`, `"ReleaseStock"`) rather than via `ConfigureEndpoints`, so the EF Core inbox can be attached for deduplication.
- Resilience: `UseMessageRetry(100, 500, 1000, 5000 ms)` then `UseCircuitBreaker` (tracking 1 min, trip 15, active threshold 10, reset 5 min), applied bus-wide. A circuit breaker that trips stays open for the full five minutes, during which that endpoint consumes nothing; the retry intervals are identical for every message, so messages that fail together retry together.
- Concurrency: reserve, release and the inbox/outbox run in a `READ COMMITTED` transaction (`InventoryOutbox`), and each consumer takes the item's row lock first (`GetByProductIdForUpdateAsync`, `SELECT … FOR UPDATE`). The default SERIALIZABLE isolation made concurrent reservations of one product fail with Postgres `40001` and exhaust their retries (40 at once: 37 failed). The restock endpoint takes the same lock, inside its own transaction (`IInventoryRepository.InTransactionAsync`), so a restock queues behind an in-flight reservation instead of overwriting it.
- Restock has no upper bound and no audit trail beyond the log line.

## OrderService

Endpoints (all authenticated): `GET /orders` (caller's orders only), `GET /orders/{id}`, `POST /orders`. Admin only: `GET /orders/admin/summary?from=&to=` (counts by status, revenue over confirmed orders, average order value and cancellation rate for the period and the equal period before it; `from` inclusive, `to` exclusive, UTC, at most 366 days; a missing, reversed or too long range is a 400). See [admin-dashboard-plan.md](admin-dashboard-plan.md).

- `GetByIdAsync` returns `null` — surfaced as `404`, not `403` — when the order belongs to another user, so ownership is enforced without leaking existence.
- `CreateAsync` builds `Order` + `OrderItem`s from the request, adds it, publishes `OrderSubmitted`, then one `SaveChangesAsync` commits the order and the outbox message atomically.
- **`UnitPrice` is checked against CatalogService before an order is accepted** (`IProductPriceProvider`, implemented by `CatalogClient` over `GET /products/{id}`, base address `Catalog:BaseUrl`, which is required at startup). The client still sends a price, as a claim: an unknown product is refused with `422`, a price that differs from the catalog with `409` (so nobody is charged an amount other than the one they were shown), and an unreachable catalog with `503` (no retry, 5 s timeout). Nothing is stored or published in those cases. The price is then stored on the line as before, so later catalog changes do not alter existing orders. `TotalAmount` is computed from those values (`Sum(Quantity * UnitPrice)`, not persisted — `Ignore`d in EF).
- `Order` invariants: requires a non-empty `UserId` and ≥1 item; `Confirm()` and `Cancel(reason)` are **idempotent for the same state** (they return whether they changed anything; `Cancel` keeps the first reason) and **throw `InvalidOperationException` for the opposite terminal state**. Status: `Pending → Confirmed | Cancelled`, terminal.
- Hosts the `OrderSagaStateMachine` with the EF Core saga repository in its own DbContext, plus the transactional outbox (`UseBusOutbox`) and, on the saga's own receive endpoint, the EF inbox and outbox (`UseEntityFrameworkOutbox`, added through `AddConfigureEndpointsCallback` in `Program.cs`). See [order-flow.md](order-flow.md).
- Runs two `BackgroundService`s: `OrderMetricsCollector` (refreshes the saga/outbox gauges) and `CheckoutTimeoutSweeper` (cancels checkouts older than `Checkout:Timeout`, default 5 min, swept every `Checkout:SweepInterval`, default 30 s; also deletes `TimedOut` sagas past `Checkout:TimedOutRetention`, default 24 h). Both are safe with several replicas, but the sweeper only runs while at least one replica is up.
- Resilience: `UseMessageRetry(100, 250, 500, 1000 ms)` — sized for Postgres serialization conflicts (`40001`) when two stock responses hit the same saga row — then the same circuit-breaker settings as InventoryService.
- `EnableRetryOnFailure` is deliberately **not** used here (nor in User/Inventory): EF's retrying execution strategy is incompatible with the explicit transactions MassTransit's saga repository and inbox/outbox open. Message-level retry covers that class of failure instead.

## Testing

- Unit tests per service cover domain invariants (`Order`, `OrderItem`, status transitions, `InventoryItem`, `Basket`, `Product`, `User`), application services, `ReserveStockConsumer`, and `Pbkdf2PasswordHasherTests`.
- `tests/Saga.IntegrationTests/CheckoutSagaTests.cs` is the strongest behavioural evidence in the repo: real Postgres ×2 + real RabbitMQ via Testcontainers, OrderService hosted through `WebApplicationFactory`, InventoryService's consumers hosted as a bare `IHost`. It asserts both the confirm path (stock decremented) and the compensation path (the successfully-reserved item's stock is restored). InventoryService is referenced under the `InventoryAlias` extern alias because both services' top-level `Program` types would otherwise collide.
- No tests exist for the gateway, authentication/authorization at the HTTP level, or the UserService→TaskService `UserRegistered` flow.
