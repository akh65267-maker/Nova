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

Endpoints: `GET /products` (filter/sort/page), `GET /products/{id}`, `POST /products` (`Admin`), and a picture per product: `GET /products/{id}/image` (public), `PUT /products/{id}/image` and `DELETE /products/{id}/image` (`Admin`).

**Product pictures.** A product has at most one. `PUT` takes the picture itself as the request body (not a form), at most **2 MB**; the type is decided from the **bytes** (JPEG, PNG or WebP), never from the Content-Type the caller sent, and SVG is refused because it can carry script (415; 413 over the limit). The file is stored as `{productId}-{version}` in `ProductImages:Directory` (a Docker volume, `taskflow-product-images`, in compose), where `version` is the first 16 hex characters of the picture's SHA-256: the name is built from a Guid and hex only, so nothing a caller sends reaches another path. The product row keeps only the content type and the version (two nullable columns), and `ProductDto.imageUrl` is `/products/{id}/image?v={version}` or `null`. Because the URL changes when the picture does, `GET` with the right `v` answers `public, max-age=31536000, immutable` (with an ETag; `If-None-Match` gets 304), and without one `public, no-cache`. The file is written before the row is saved (a row never points at a missing file; a failed save leaves an orphan file, which costs space only), and the old file is deleted after. No resizing or metadata stripping happens yet: what was uploaded is what is served, so the 2 MB limit is the only bound on size, and a photo's EXIF data (including location) stays in it. The files live on one volume, so this service cannot be scaled to several replicas without moving them to shared storage (object storage) behind `IProductImageStore`.

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

Endpoints: `GET /inventory/{productId}` (public: a status, and the exact `remaining` only when it is 10 or fewer), `GET /inventory` and `GET /inventory/{productId}/quantity` (`Admin`: exact quantities), `POST /inventory` (`Admin`), `POST /inventory/{productId}/restock` (`Admin`).

- `InventoryItem` is keyed by `ProductId` and holds a single `QuantityAvailable`. Business rules live in the entity: `Reserve` rejects non-positive quantity and throws `InsufficientStockException` when `quantity > QuantityAvailable`; `Release` and `Restock` both add (identical behaviour, kept as separate intents).
- There is **no separate "reserved" counter** — reserving decrements available stock, and compensation adds it back.
- Consumers: `ReserveStockConsumer`, `ReleaseStockConsumer` (see [messaging.md](messaging.md)). Endpoints are declared explicitly (`ReceiveEndpoint("ReserveStock")`, `"ReleaseStock"`) rather than via `ConfigureEndpoints`, so the EF Core inbox can be attached for deduplication.
- Resilience: `UseMessageRetry(100, 500, 1000, 5000 ms)` then `UseCircuitBreaker` (tracking 1 min, trip 15, active threshold 10, reset 5 min), applied bus-wide. A circuit breaker that trips stays open for the full five minutes, during which that endpoint consumes nothing; the retry intervals are identical for every message, so messages that fail together retry together.
- Concurrency: reserve, release and the inbox/outbox run in a `READ COMMITTED` transaction (`InventoryOutbox`), and each consumer takes the item's row lock first (`GetByProductIdForUpdateAsync`, `SELECT … FOR UPDATE`). The default SERIALIZABLE isolation made concurrent reservations of one product fail with Postgres `40001` and exhaust their retries (40 at once: 37 failed). The restock endpoint takes the same lock, inside its own transaction (`IInventoryRepository.InTransactionAsync`), so a restock queues behind an in-flight reservation instead of overwriting it.
- Restock has no upper bound and no audit trail beyond the log line.

## OrderService

Endpoints (all authenticated): `GET /orders` (caller's orders only), `GET /orders/{id}`, `POST /orders`. Admin only, all under `/orders/admin`: `summary`, `timeseries`, `cancellations`, `top-products`, `pending`, `orders` (a paged list), and `orders/{id}` (any customer's order with its lines; the ordinary `GET /orders/{id}` stays the owner's only). Every list one takes a UTC range (`from` inclusive, `to` exclusive, at most 366 days) except `pending`; a missing, reversed, too long or malformed value is a 400. `summary` is: (counts by status, revenue over confirmed orders, average order value and cancellation rate for the period and the equal period before it; `from` inclusive, `to` exclusive, UTC, at most 366 days; a missing, reversed or too long range is a 400). See [admin-dashboard-plan.md](admin-dashboard-plan.md).

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

## AssistantService

`POST /assistant/chat` (signed-in users; body `{ "message": "…" }`, 1–1000 characters) answers in `{ reply, toolsUsed }`. It is a Microsoft Agent Framework agent (`ChatClientAgent`) over an `IChatClient`: a local Ollama through OllamaSharp by default, or the Anthropic API and two **read-only** tools it may call, `search_products` (the public catalog, minus products that are out of stock or have no stock record, checked against InventoryService via `Assistant:InventoryUrl`) and `get_my_orders` (forwarded with the **caller's own token**, so it sees what the caller can and nothing more: never another customer's orders). The shop keeps the cart in the browser, not in the basket service, so the browser sends the cart with each question (`cart`: name, price, quantity; at most 20 lines, names cut to 80 characters and stripped of line breaks) and the runner states it to the model; there is no basket tool. The prompt asks it to recommend only what the tools returned and to leave health questions to a professional, suggesting products only by general features. What customers *in general* prefer is analytics (counts), not a model's job, and would be an admin-only tool. `AssistantRunner` asks the model, runs the tools it names (an unknown name gets "There is no such tool"), feeds the results back, and gives up after 4 rounds. It cannot place orders or change anything.

**Model provider.** `Assistant:Provider` is `ollama` (default, local, free) or `anthropic` (the Anthropic SDK as an `IChatClient`; needs `Assistant:Anthropic:ApiKey`, model in `Assistant:Anthropic:Model`, default `claude-haiku-5-5`). In compose: `ASSISTANT_PROVIDER=anthropic` and `ANTHROPIC_API_KEY=…` in `deploy/.env` (never commit the key); the `ollama` container is then not needed. Both are an `IChatClient`, so the agent and tools are unchanged.

It is **off** unless `Assistant:Enabled` is true (503 otherwise), so the service can ship before a model is ready. In compose: set `ASSISTANT_ENABLED=true`, start the `ai` profile (`docker compose --profile ai up -d`) and pull the model once (`docker exec taskflow-ollama ollama pull qwen2.5:7b`; `ASSISTANT_MODEL` changes it). A small model on a CPU answers in seconds to a minute, so the model call has a 120 s timeout and a model that is down is a 503, not a 500. Unit tests use a scripted fake model; nothing in CI needs Ollama.
