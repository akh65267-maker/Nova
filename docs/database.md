# Data & persistence boundaries

Database-per-service, enforced physically: `deploy/docker-compose.yml` runs a **separate Postgres container per service**. No service can read another's data, and there are no cross-service foreign keys.

| Service | Store | Connection string key | Compose DB / container | Host port |
|---|---|---|---|---|
| TaskService | Postgres 17 | `TaskDatabase` | `taskflow` / `postgres` | 5432 |
| UserService | Postgres 17 | `UserDatabase` | `userflow` / `postgres-users` | 5433 |
| CatalogService | Postgres 17 | `CatalogDatabase` | `catalogflow` / `postgres-catalog` | 5434 |
| InventoryService | Postgres 17 | `InventoryDatabase` | `inventoryflow` / `postgres-inventory` | 5435 |
| OrderService | Postgres 17 | `OrderDatabase` | `orderflow` / `postgres-order` | 5436 |
| BasketService | Redis 7 | `Redis` | `redis` | 6379 |

Each Postgres service has its own named volume; Redis has **none** — basket data is lost when the container is recreated.

## Ownership boundaries that matter

- `ProductId` is the join key between Catalog (product definition + price) and Inventory (stock), but it is **only a convention**. Creating a product does not create an inventory record, and `ReserveStock` for an unknown product is answered with `StockReservationFailed("No inventory record for this product.")` rather than an error.
- Order line items store their **own copy of `UnitPrice`**, confirmed against the catalog when the order is placed (see [services.md](services.md)). Orders are therefore immune to later catalog price changes.
- `UserId` appears in Basket, Order and the saga, with no foreign key to UserService. Deleting a user (no endpoint exists) would orphan those rows.

## Schemas

**OrderDbContext** (`orderflow`)
- `Orders` — PK `Id`; `Status` persisted as a `string` (max 20) via `HasConversion<string>`; `TotalAmount` is `Ignore`d (computed); `Items` mapped as `OwnsMany` with a shadow `Guid Id` PK and shadow FK `OrderId`; `UnitPrice` `HasPrecision(18,2)`.
- `OrderSagaStates` — PK `CorrelationId`; `RowVersion` as `IsRowVersion()`; `CurrentState` max 64 (`AwaitingStockReservation` or `TimedOut`; a finalized saga's row is deleted); `ItemsJson`/`ReservedProductIdsJson` required; `CreatedAtUtc` (`timestamptz`, DB default `now()`) is what the checkout timeout is measured from.
- MassTransit `InboxState`, `OutboxState`, `OutboxMessage`.

**InventoryDbContext** (`inventoryflow`)
- `InventoryItems` — **PK is `ProductId`** (no surrogate key), plus required `QuantityAvailable`. No reserved-quantity column and no row-version / concurrency token. Safety under concurrent reservation comes from a row lock: the consumers read the row with `SELECT … FOR UPDATE` inside a `READ COMMITTED` transaction, so concurrent writers queue on the lock rather than failing (the previous SERIALIZABLE default made all but one fail with `40001`). The restock endpoint takes the same lock inside its own transaction, so it queues with them too.
- MassTransit `InboxState`, `OutboxState`, `OutboxMessage`.

**UserDbContext** (`userflow`) — users plus the MassTransit outbox tables (outbox only; no consumers).

**CatalogDbContext** (`catalogflow`) — products only; no MassTransit tables (the service has no bus).

**Redis (Basket)** — one string key per user, `basket:{userId}`, holding the whole `Basket` object as JSON. No TTL, no secondary indexes.

## Connection pools

Every service caps its Npgsql pool with `Maximum Pool Size` in the connection string (`deploy/docker-compose.yml`; `postgresMaxPoolSize` in the Azure template). Npgsql's default is 100, which equals Postgres's default `max_connections` of 100, so one busy service could take every slot and leave nothing for its own background work, monitoring or an admin. Local defaults: order 80, inventory 30, catalog 30, users 20, each overridable (`ORDER_DB_POOL_SIZE` and so on). Sizing rule: pool x replicas, summed over the services that share a server, below `max_connections` with headroom.

**The cap is a trade, not a free fix.** Measured with the stress profile (`STEPS=100,200,300,400,600 MAX_VUS=1200`) on the order service:

| Order pool | Highest step that held | Order DB connections, peak | Result |
|---|---|---|---|
| 100 (default) | 400 it/s | 100 of 100, monitoring refused for ~4 min | stalls above 400; all orders eventually confirmed |
| 80 | 300 it/s | 82 | invariants hold; `rate-400` 68% of checkouts confirmed |
| 40 | 300 it/s | 42 | invariants hold; `rate-400` 50% |

Single runs, so read the direction, not the decimals. Throughput follows the connections the service may use. At peak, most of them were *idle in transaction* (31 at pool 80), not running queries (25): connections are held by transactions that are waiting, and shortening that wait (what the saga and outbox transactions block on) is the real capacity lever, not the pool size. The first capped run was invalid for a different reason: the order Postgres crashed during it (see [TODO.md](TODO.md)).

## Migrations

EF Core migrations are committed for Catalog, Inventory, Order, User and Task under each service's `Migrations/` folder.

**No service applies migrations at startup** — there is no `Database.Migrate()` or `EnsureCreated()` call anywhere in `src/`. Schema must be applied out of band (`dotnet ef database update`, or an equivalent step); `docker compose up` alone leaves the databases empty and services failing on first query. `Saga.IntegrationTests` calls `MigrateAsync()` itself, and does so through a standalone `DbContext` rather than the test host's provider — touching `WebApplicationFactory.Services` starts the MassTransit outbox poller, which then fails against tables that don't exist yet.

To get a **fresh, disposable stack with the schema applied** (for the frontend's end-to-end tests, in CI or locally), run `deploy/scripts/e2e-stack.sh up`: it writes a `deploy/.env` of random secrets (refusing if one exists), starts the databases, runs the migration image against them, then starts the services and seeds one product. `down` deletes it all, data included. The migration image is the same one the Azure migration job uses, so this also proves it still builds and runs: it caught the image failing on OrderService once `Catalog:BaseUrl` became required, which the entrypoint now supplies a placeholder for.
