# Review Plan — TaskManager (.NET) & taskmanager-web (Next.js)

> Review only. Nothing has been changed. Priority: 🔴 High · 🟠 Medium · 🟢 Low

---

## 1. Backend — `TaskManager`

### ✅ Approved (keep as is)
- **Service boundaries**: Catalog, Order, Inventory, Basket, User, Task, Assistant + YARP Gateway, a database per service, shared `Contracts` project for messages.
- **Messaging**: MassTransit saga with EF outbox/inbox (`OrderService/Program.cs`). The comments explain why it is there (race conditions, duplicate redelivery). Good design.
- **Order pricing**: `OrdersService.EnsurePricesMatchCatalogAsync` re-checks the price the client sent against the catalog, and the order plus the `OrderSubmitted` message are saved in a single `SaveChanges`.
- **Security basics**: JWT validation turns on issuer/audience/lifetime checks, admin endpoints check roles, there is a login lockout policy, refresh tokens are rotated and cleaned up, passwords use PBKDF2, and forwarded headers are only trusted from known networks.
- **Upload safety**: image uploads have a size limit and are read into a fixed-size buffer.
- **Quality gates**: unit tests for every service, Testcontainers integration tests for the saga, a CI check for migration drift, and load/chaos test results.
- **Observability**: the shared `Observability` project, Serilog, Prometheus/Grafana dashboards.

### 🔴 High priority
| # | Issue | Where | Refactor |
|---|-------|-------|----------|
| B1 | **`bin/` and `obj/` are committed to git** (e.g. `UserService/bin/Debug/net8.0/UserService.dll`) | repo | Add a .NET `.gitignore` and run `git rm -r --cached **/bin **/obj` |
| B2 | **Passwords that look real are committed** in `appsettings.Development.json` (DB, RabbitMQ, seed admin) | User/Catalog services | Move them to `dotnet user-secrets` or `.env`, rotate the leaked values, and keep only placeholders in the repo |
| B3 | A `secret.md` file with e2e credentials sits in the repo root (it is untracked, but one `git add .` would commit it) | root | Move it out of the repo or add it to `.gitignore` |
| B4 | The same JWT signing key is in every service, and the issuer falls back to `"TaskManager"` if config is missing | all `Program.cs` | Make issuer/audience required, take the key from secrets only, and consider asymmetric keys (RS256 + JWKS from UserService) |
| B5 | No rate limiting at the gateway (only the login lockout) | `ApiGateway/Program.cs` | Add `AddRateLimiter` (per IP / per user) on auth and write routes |

### 🟠 Medium priority — refactors
| # | Issue | Where | Refactor |
|---|-------|-------|----------|
| B6 | **Duplicated setup**: the JWT auth block, RabbitMQ host setup and health checks are copied into each `Program.cs` | 6 services | Move them into shared extensions (`AddJwtAuth()`, `AddRabbitMq()`) in a `Shared/ServiceDefaults` project |
| B7 | **Duplicated classes**: `ApiExceptionHandler` (×6), `ClaimsPrincipalExtensions` (×3) and `DatabaseOutage` (×2) are near copies | services | Move them into a shared library, with per-service mapping where needed |
| B8 | **`CatalogService/Program.cs` is 443 lines** with every endpoint inline | Catalog | Split into endpoint groups (`MapGroup("/products")`, `ProductEndpoints.cs`, `CategoryEndpoints.cs`, `ReviewEndpoints.cs`) |
| B9 | The image upload buffer loop is copied (main image + gallery) | Catalog `Program.cs` | Pull it out into a `ReadBoundedBodyAsync` helper |
| B10 | Inconsistent application style: Catalog uses MediatR/CQRS, the others use `XxxService` classes | services | Pick one convention and write it down (both are fine; mixing them is the problem) |
| B11 | Middleware order: `UseExceptionHandler()` and `UseHttpsRedirection()` are registered after auth and the endpoint mappings | Catalog (check the others) | Register the exception handler first and HTTPS redirection near the top |
| B12 | The Order→Catalog `HttpClient` has only a 5 s timeout and no retry or circuit breaker | `OrderService` `CatalogClient` | See **A1-a** in section 3 |
| B13 | Query parameters are parsed by hand from `HttpRequest.Query` | `GET /products` | Bind them to a record with `[AsParameters]` |
| B14 | The domain doesn't match the name: the solution is `NovaCommerce.slnx` (e‑commerce) but the repo is "TaskManager", and `TaskService` lives next to shop services | repo | Agree on the naming; remove `TaskService` or explain why it is there |

### 🟢 Low priority
- Remove `.vs/` from the repo; the old `NovaCommerce` layout files are still in it.
- Move `loadtests/results/*.json` to CI artifacts instead of committing them.
- Add `Directory.Build.props` (nullable, warnings as errors, analyzers) and `Directory.Packages.props` (central package versions).
- No integration tests for the Basket or Assistant service APIs.

---

## 2. Frontend — `taskmanager-web`

### ✅ Approved
- **Monorepo layout**: `apps/web`, `apps/admin` + `packages/{api,auth,session,ui,config}`, set up with npm workspaces.
- **BFF session design** (`packages/session`): tokens are kept in httpOnly cookies, there is an origin check, a refresh coordinator that runs one refresh at a time (shared across route bundles), the role check is enforced on the server, and a demoted admin loses access. Strong, and well tested (`handlers.test.ts`).
- Shared UI kit (shadcn-style), React Query hooks in `packages/api`, a central `getApiErrorMessage`.
- Playwright e2e tests for both apps, plus husky + lint-staged.

### 🔴 High priority
| # | Issue | Where | Refactor |
|---|-------|-------|----------|
| F1 | **Almost every page is a client component** (18 `"use client"` files for 16 pages), product detail included | `apps/web/src/app/**` | Make catalog, product detail and home into Server Components that fetch on the server (`catalog-server.ts` already exists); keep only the interactive parts on the client. Better SEO, first paint and bundle size |
| F2 | `next.config.ts` is empty: no security headers or CSP, no `images.remotePatterns` | both apps | Add `headers()` (CSP, X-Frame-Options, Referrer-Policy) and image config |

### 🟠 Medium priority — refactors
| # | Issue | Where | Refactor |
|---|-------|-------|----------|
| F3 | **`admin/products/page.tsx` is 802 lines** with about 12 components inline (dialogs, discount, gallery, restock, picture) | admin | Split into `features/products/*` (one file per dialog or control) |
| F4 | The image/gallery picker logic is repeated (`GalleryField`, `PictureControl`) | admin products | Build a shared `useImageUpload` hook or `<ImageUploadButton>` |
| F5 | 21 `useEffect`s in `apps/web` | e.g. `cart-sync.tsx`, product page | Check each one; replace data fetching or derived state with React Query or computed values |
| F6 | The `use(params)` client pattern for `[id]` pages | `products/[id]`, `orders/[id]` | Move to server pages with `generateMetadata` |
| F7 | Build leftovers are checked in or not ignored: `playwright-report/`, `test-results/`, `tsconfig.tsbuildinfo` | `apps/web` | Add them to `.gitignore` |
| F8 | Naming: the package scope is `@taskmanager/*` but the product is a shop | all | Line it up with the backend naming decision (B14) |

### 🟢 Low priority
- Each workspace has its own `lint-staged` entry; one root ESLint flat config would do.
- Add `typecheck` and a `turbo`/`nx` cache to the root scripts, if build time becomes a problem.
- The admin `bar-chart.tsx` is hand-made; fine for now, but check accessibility (labels, contrast).

---

## 3. Architecture drift — services call each other over REST, the docs say they don't

**The docs say:** `docs/architecture.md:3` says there is "no service-to-service HTTP call anywhere in the code", and `docs/decisions.md:11-12` says RabbitMQ is "the only inter-service channel" and that there is "no `HttpClient` … targets another service".

**What the code does:**

| # | Caller → Called | Where | What it does |
|---|-----------------|-------|--------------|
| A1 | **Order → Catalog** | `OrderService/Infrastructure/Catalog/CatalogClient.cs`, `Program.cs:54-60` (`Catalog:BaseUrl`) | While an order is being created, sends one `GET products/{id}` per product, all in parallel, to check prices. No retry, no circuit breaker. If Catalog is down, **checkout fails**. |
| A2 | **Assistant → Catalog** | `AssistantService/Application/AssistantTools.cs` (`SearchProductsTool`), `Program.cs:41` | `GET /products?search=…` |
| A3 | **Assistant → Inventory** | same file, `IsAvailableAsync`, `Program.cs:42` | **N+1 calls, one after another**: one `GET /inventory/{id}` for each product found |
| A4 | **Assistant → Order** | `GetMyOrdersTool`, `Program.cs:43` | `GET /orders` and passes on the user's JWT |

Catalog itself makes no outgoing calls. It shows up here because it is the **service being called** (A1, A2), so its public HTTP API has become an internal contract that nobody has written down.

**Why it matters:** it couples services at runtime. Catalog being down now breaks checkout and the assistant; in the chaos results there are test runs for inventory, order-db, rabbitmq and redis going down, but **none for catalog going down**. Calls go straight to service URLs and skip the gateway. And the docs mislead anyone who reads them.

**Recommendations:**

| # | Prio | Action |
|---|------|--------|
| A1-a | 🔴 | **Decide for Order→Catalog and write the decision down (ADR in `decisions.md`).** Option 1 (fits the docs): Catalog publishes `ProductPriceChanged`/`ProductCreated`/`ProductArchived` events, Order keeps a local `ProductPrices` read model and checks prices against it, so checkout no longer needs Catalog to be up. Option 2: keep the synchronous price check because it gives the freshest price, but record it as an accepted exception and add `AddStandardResilienceHandler` (short timeout + circuit breaker) and use the existing `GET /products?ids=` for one batch call instead of N calls. |
| A2-a | 🟠 | Assistant is really a read-side aggregator (a BFF for the model), so synchronous reads are reasonable here. **Record it as an accepted exception** and route its calls **through the gateway** instead of straight to service URLs, so they get the same auth, CORS and rate limits. |
| A3-a | 🟠 | Replace the per-product inventory loop with one batch call (add `GET /inventory?ids=`), or at least run the calls in parallel with a limit. |
| A4-a | 🟢 | Passing the user's JWT on is fine. Check that only the user's own `Authorization` header is forwarded and that it is never logged. |
| A-docs | 🔴 | Fix `architecture.md:3` and `decisions.md:11-12` to match the code, list the sync calls in `services.md`, and add a "catalog down" chaos test. |

---

## 4. Deep-dive findings (second pass)

### 🐞 Bugs (correctness)
| # | Prio | Issue | Where | Fix |
|---|------|-------|-------|-----|
| D1 | 🔴 | **Wrong amount of stock released when one order has the same product on two lines.** In `FinalizeAsync`, the cancel/timeout path uses `items.FirstOrDefault(i => i.ProductId == productId)`, so each release uses the *first* line's quantity. `Order` does not merge duplicate lines. (`ReleaseLateReservationAsync` handles this case correctly; `FinalizeAsync` doesn't.) | `OrderSagaStateMachine.cs` `FinalizeAsync` | Merge lines by product in `Order` (or reject duplicates), or release per line the same way the late-release path does. Add a test. |
| D2 | 🔴 | **Assistant can recommend out-of-stock products.** `ToolHttp.GetAsync` cuts the catalog response to 4000 characters *before* `InStockOnlyAsync` parses it. Cut JSON doesn't parse, so the unfiltered list is handed to the model. | `AssistantTools.cs` | Parse and filter first, then project to small fields (id, name, price), then cut the text. |
| D3 | 🟠 | **Basket limits are only enforced on `PUT /basket`.** `POST /basket/items` has no limit on lines or quantity (`IncreaseQuantity` can even overflow). | `BasketsService.AddItemAsync` | Move `MaxLines`/`MaxQuantity` into the `Basket` domain object. |
| D4 | 🟠 | **Basket lost update**: the read → change → `StringSet` sequence has no concurrency check. Two tabs adding at the same time lose one change. | `RedisBasketRepository` | Use Redis hashes (`HINCRBY`) or a WATCH/transaction. |
| D5 | 🟠 | **Baskets never expire**: no TTL is set on `basket:{userId}`. | same | Set a sliding TTL, e.g. 30 days. |
| D6 | 🟠 | **Registration race**: two requests with the same email both pass `EmailExistsAsync`; the unique index then throws `DbUpdateException`, which becomes a **500**. | `UsersService.RegisterAsync` | Catch the unique-violation error and return 409. |
| D7 | 🟠 | **`POST /orders` is not idempotent.** A retry or double submit after a network error creates two orders, and stock is reserved twice. The button is only disabled on the client. | `OrdersService.CreateAsync`, checkout page | Accept an `Idempotency-Key` header and add a unique key per user. |
| D8 | 🟠 | **Stale prices in the cart.** The cart in localStorage stores `price`, checkout sends it, and the backend rejects any price that has changed (for example when a discount expires). Nothing re-prices the cart before submit. | `cart-store.ts`, `checkout/page.tsx` | Re-fetch prices with `GET /products?ids=` on the cart and checkout pages, and handle the price-changed error by updating the cart. |
| D9 | 🟢 | The admin seed runs at startup with check-then-insert, so two replicas starting together → one crashes on the unique index. | `UserService/Program.cs` | Move it to the migrations job, or catch the error. |
| D10 | 🟢 | The saga, `Order`, `JwtTokenGenerator` and `UsersService` (UserRegistered) call `DateTimeOffset.UtcNow` directly, although `TimeProvider` is already registered. | several | Inject `TimeProvider` so time can be tested. |

### 🔐 Security
| # | Prio | Issue | Where | Fix |
|---|------|-------|-------|-----|
| S1 | 🔴 | **Error details leak to clients.** `ApiExceptionHandler` returns `Detail = exception.Message` for every `ArgumentException`. Registration answers "A user with email 'x' already exists", which lets anyone check whether an email has an account, and framework/EF messages can expose internals. | all `ApiExceptionHandler` (×6) | Use a dedicated `ValidationException`/`DomainException` for messages meant for users, and fixed text for everything else. Pairs with B7 (one shared handler). |
| S2 | 🔴 | **No rate limit on `POST /users` (register)** or on `/assistant/chat`. Each assistant call can hold the model for up to 120 s, so it is easy to exhaust it on purpose. | UserService, AssistantService | Per-IP limit on register, and a per-user limit plus a concurrency cap on the assistant. |
| S3 | 🟠 | **No upper bound on order size**: `OrderItem` checks only `quantity > 0`, with no limit on quantity or number of lines. One request fans out to N catalog calls and N `ReserveStock` messages. The UI's `MAX_PER_ORDER = 10` is enforced only on the client. | `Order`/`OrderItem`, `CreateOrderRequest` | Enforce limits in the domain (e.g. ≤ 50 lines, ≤ 10 per line). |
| S4 | 🟠 | **PBKDF2 at 100k iterations, and the stored format has no version.** OWASP recommends 600k for SHA-256, and without a version the cost can't be raised later without breaking existing hashes. | `Pbkdf2PasswordHasher` | Store `v1.iter.salt.hash` and rehash on login when the parameters are old. |
| S5 | 🟠 | **The BFF takes the client-sent `X-Forwarded-For` as-is** (`clientAddress` uses the last entry). When the Next server is not behind a proxy, a client can fake its IP, and the gateway trusts the shop server's network, so the per-IP login limit can be bypassed. | `packages/session/request-checks.ts` | Read the XFF header only when a `TRUST_PROXY` setting is on; otherwise use the socket address. |
| S6 | 🟠 | **The BFF proxy reads the whole request body with no size limit** (`request.arrayBuffer()`) and also buffers the whole response. | `handlers.ts` `proxy` | Check `content-length` and set a maximum (e.g. 6 MB for images), and stream the response. |
| S7 | 🟠 | **Compose publishes every service, database, Redis (no password), RabbitMQ management and Jaeger on `0.0.0.0`.** That makes it easy to skip the gateway on any machine running the stack. | `deploy/docker-compose.yml` | Bind to `127.0.0.1:` and publish only the gateway/web; add `requirepass` to Redis. |
| S8 | 🟢 | The assistant puts customer-supplied cart names and catalog text into the prompt (prompt-injection risk). Line breaks are already removed. | `AssistantRunner.DescribeCart` | Keep the tools read-only (as now), and mark the untrusted data clearly in the prompt. |
| S9 | 🟢 | Refresh doesn't check whether the account is locked or disabled; only a deleted user is caught. | `SessionService.RefreshAsync` | Decide on a policy (e.g. revoke the sessions when an account is locked). |

### ⚙️ Performance / operability
| # | Prio | Issue | Where | Fix |
|---|------|-------|-------|-----|
| P1 | 🟠 | Product search uses `ILIKE '%term%'` on Name/Brand → a full table scan. `%` and `_` in the search text are not escaped. | `ProductRepository.SearchAsync` | Add a `pg_trgm` GIN index (or Postgres full-text search) and escape the wildcards. |
| P2 | 🟠 | Product images are stored on the **local disk** (`FileProductImageStore`), so Catalog can't run more than one replica. | Catalog | Put Azure Blob/S3 behind `IProductImageStore` (the interface already exists). |
| P3 | 🟠 | `CheckoutTimeoutSweeper` and `RefreshTokenCleanupService` run on **every replica**, so work is duplicated and the same timeouts are published several times (harmless today, wasteful when scaled). | Order, User | Use a leader lock (`pg_try_advisory_lock`). |
| P4 | 🟢 | `/health` checks the database or Redis but **not RabbitMQ**; MassTransit's own health check isn't mapped. | services with the bus | Register the MassTransit health checks with a readiness tag. |
| P5 | 🟢 | `CatalogService` reads image uploads into a 5 MB+ `byte[]` for every request. | Catalog `Program.cs` | Stream to a temp file or use `ArrayPool`. |
| P6 | 🟢 | Floating image tags: `jaegertracing/all-in-one:latest`, `rabbitmq:3-management`. | compose | Pin the versions. |
| P7 | 🟢 | Each Dockerfile runs `dotnet build` and then `dotnet publish` (it builds twice). | Dockerfiles | Run `publish` only. |

### ✅ Also approved on the deep pass
- Refresh-token design: SHA-256 hashed storage, rotation through an atomic `TryMarkUsed`, a reuse grace period then **revoking the whole family**, sliding plus absolute lifetimes.
- Login: the same answer and timing for unknown, locked and wrong-password cases (dummy hash), a lockout that isn't extended by refused attempts, and rate limiting keyed by the forwarded client IP.
- The saga's defences: ignoring duplicate `OrderSubmitted`, the `TimedOut` tombstone state that releases late reservations, finalization that doesn't throw on conflicts, timeouts from a sweeper instead of a broker scheduler, and the inbox/outbox reasoning.
- Inventory uses `SELECT … FOR UPDATE` row locks and calls `SaveChanges` on every path so the inbox entry is always recorded.
- TaskService checks ownership on every read and write, and returns 404 instead of 403 so it doesn't reveal which ids exist.
- `FileProductImageStore`: file names are built only from a Guid and hex text, and writes go to a temp file and are then moved into place.
- BFF: cookies scoped to `/api`, HttpOnly and SameSite=Lax, an Origin check on every request that changes data, the role checked again on each refresh, and the gateway never sees the browser's cookies.
- Dockerfiles run as non-root (`USER $APP_UID`) and have `.dockerignore` files; the deploy workflow takes its secrets from GitHub `secrets`.

---

## 5. Suggested order
1. **B1–B3** (repo hygiene and secrets): do these right away, before anything else.
2. **D1, D2, S1, S2** (real bugs and security problems), then **A-docs + A1-a decision** (architecture drift).
3. **F2, B4, B5** (security hardening).
4. **B6–B9, F3–F4** (removing duplication and splitting large files): mechanical, low risk.
5. **F1, F6** (moving to Server Components): the biggest frontend gain; do it page by page.
6. **D3–D8, S3–S7, P1–P3** (correctness and hardening).
7. **A2-a, A3-a, B10–B14, F5, F7–F8, D9–D10, S8–S9, P4–P7** and the low-priority items.

> Scope note: I read the structure, all `Program.cs` sizes, the Catalog/Order/Gateway programs, `OrdersService`, config files, CI, and the main frontend files (session handlers, admin products page, app layout). I didn't read every handler or component line by line, so treat the medium and low items as "verify, then fix".
