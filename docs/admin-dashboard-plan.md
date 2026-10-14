# Admin dashboard, orders analytics and the frontend split

Status: **the monorepo extraction (section 4.3, steps 1 to 5) is built, on the `monorepo` branch of the frontend repository. Backend: the `ResolvedAtUtc` column and the three indexes (one migration), and all seven admin endpoints (`summary`, `timeseries`, `cancellations`, `top-products`, `pending`, `orders`), are built and tested; the 30-second cache is not built (see 2.5). The admin app has the Dashboard (cards, daily charts, cancellations, stuck orders, low stock) and the Orders section (list with filters, order detail).** The admin app (port 3200, its own sign-in, the products page) exists, and the gateway's CORS list includes it. Covers items 3 and 4 of "Found by hand-testing" in [TODO.md](TODO.md): admins should see orders analytics, and there should be an admin dashboard, which is also the point at which the frontend should become a monorepo with a separate admin app (the trigger recorded in the frontend repo's `docs/frontend.md`, Admin Panel).

Effort figures are rough guesses from the size of similar work in this repository, not measurements.

## 1. What the data can and cannot answer today

An order records: id, user id, status (`Pending`/`Confirmed`/`Cancelled`), created time, a cancellation reason, and its lines (product id, quantity, unit price). Products are in CatalogService, stock in InventoryService, users in UserService, each in its own database.

| Question | Answerable now? |
|---|---|
| Orders per day / week, by status | Yes |
| Revenue (confirmed orders), average order value | Yes (single currency, shown as `$` everywhere today) |
| Cancellation rate and the reasons | Yes (`CancellationReason`) |
| Orders stuck in Pending right now, and how old | Yes |
| Top products by units or revenue | Yes, but names need a lookup in CatalogService |
| Low stock | Yes, from InventoryService (`GET /inventory`, already public) |
| **How long checkouts take to resolve** | **No.** Nothing records when an order was confirmed or cancelled. Needs a new `ResolvedAtUtc` column (see 2.3). Orders resolved before that column exists stay unknown |
| Revenue per customer, repeat customers | Yes by user id; names/emails live in UserService |
| Abandoned carts | **No.** The cart lives in the browser; the server never sees it. Nothing to measure |
| Traffic, conversion from visits | **No.** No web analytics exists |

Two limits to keep in view: revenue here is the sum of `quantity x unitPrice` on **confirmed** orders at the price charged, with no tax, shipping, discounts or refunds, because none of those exist in the system. And stock reserved for pending orders is not "sold" yet.

## 2. Backend

### 2.1 Where it lives, and why not elsewhere

In OrderService, as admin-only endpoints under `/orders/admin/...`. The gateway already routes `/orders/{**catch-all}`, so no gateway change is needed. The aggregate queries run against OrderService's own database with plain SQL `GROUP BY`; no new store, no event pipeline.

A separate read model (events into an analytics table, or a replica) is the right answer at a scale this system does not have. It is cheap to add later behind the same endpoints, so it is deliberately not built now. Revisit when an analytics query shows up as load in the connection and latency numbers the load tests already collect.

### 2.2 Endpoints (all require the `Admin` role)

| Endpoint | Returns |
|---|---|
| `GET /orders/admin/summary?from=&to=` | Counts by status, revenue, average order value, cancellation rate for the period, plus the same figures for the previous period of equal length (so the dashboard can show a change) |
| `GET /orders/admin/timeseries?from=&to=&bucket=day` | Per bucket: orders by status and revenue. UTC buckets; `day` first, `hour` only if a use appears |
| `GET /orders/admin/cancellations?from=&to=` | Cancelled orders grouped by reason, with counts |
| `GET /orders/admin/top-products?from=&to=&by=units|revenue&limit=` | Product id, units, revenue. **Names are not stored here**: the page resolves them with the catalog (`GET /products/{id}`), or the endpoint is later given a catalog lookup like the price check has |
| `GET /orders/admin/orders?status=&from=&to=&page=&pageSize=` | A paged list of every order for the Orders section: id, status, created, total, line count, user id (not email: that is UserService's) |
| `GET /orders/admin/pending` | Pending orders older than N seconds, oldest first: the "stuck" list |

Limits that protect the database: `from`/`to` required with a maximum range (for example 366 days), `limit` capped, `pageSize` capped at 100, all parameters validated and parameterized. Dates are UTC and the response says so.

None of these returns personal data beyond a user id. Names and emails stay behind `GET /users/{id}` (admin may read any).

### 2.3 Schema changes (one migration)

- `Orders.ResolvedAtUtc` (nullable): set when the order is confirmed or cancelled. This is what makes checkout-latency and "time to resolve" answerable. It needs `Order.Confirm()`/`Cancel()` to set it, so it is a small domain change with unit tests. Existing rows stay null; the dashboard must say "not recorded" for them, not show zero.
- Indexes, which do not exist today (the table has only its primary key): `(CreatedAtUtc)`, `(Status, CreatedAtUtc)`, and `(UserId, CreatedAtUtc)`. The last also fixes the existing "my orders" query, which filters on `UserId` with no index and no paging. Index creation on a large table should be `CONCURRENTLY`; at the current size an ordinary migration is fine, and the migration should say which it is.
- Revenue per product needs the line table: `OrderItem` is joined on `OrderId` already (primary key side), plus `(ProductId)` if the top-products query is slow in practice. Measure before adding it.

### 2.4 Tests

Written first, each failing before the code exists:
- Aggregations against a real Postgres (the integration test project already starts one): a known set of orders across days, statuses and prices gives exact counts, revenue and buckets, including empty days, the previous-period comparison and a period with no orders.
- Authorization: a customer, an anonymous caller and an admin against every endpoint (403, 401, 200), as was done for `/users`.
- Validation: reversed or oversize ranges, huge `pageSize`, unknown `by` all answer 400, not a slow query.
- `ResolvedAtUtc` set on confirm and on cancel, never overwritten by an idempotent repeat.

### 2.5 Load

Aggregates scan the order table, so they are the first thing that could hurt the checkout path. Two measures: the indexes above, and a short in-process cache (for example 30 s) on the summary and timeseries so a dashboard open in five tabs is one query. After building, run the existing `load` profile with a script polling the admin endpoints and compare checkout latency with and without it. Not assumed fine.

**Measured (2026-10-14, local stack, about 57,000 orders in the table).** `loadtests/run.sh load` twice, 20 shoppers, 3 minutes steady: once alone, once with `DASHBOARD_VUS=3` (three administrators each requesting all six endpoints, 90-day ranges, every couple of seconds). Checkout was not slowed: time to resolve p50 270 ms / p95 291 ms alone, 269 ms / 289 ms with the dashboards open; every checkout confirmed in both, all invariants held, order database peak 5 then 9 connections of 100. The analytics requests themselves took p50 34 ms, p95 229 ms, max 676 ms. The limits of that: one machine, a table about 100 times smaller than a busy shop's, and three pollers. It shows no sign of trouble at this size; it does not show how the queries behave on millions of orders. For that reason the 30-second cache is **not built**; add it, or the read model above, when the same run on a bigger table shows the analytics latency or the checkout latency moving.

## 3. Frontend

### 3.1 Information architecture

The admin app has three sections (the NAV array in `app/admin/layout.tsx` already supports adding them):

1. **Dashboard**: KPI cards (orders, revenue, average order value, cancellation rate, each against the previous period), a daily orders-and-revenue chart, cancellations by reason, a "stuck in Pending" list, and low-stock products.
2. **Orders**: a table of all orders with filters (status, date range), paging, and an order detail view (lines, status, reason, user id).
3. **Products**: what exists today (list, create, restock), plus edit and delete when the backend supports them.

A period picker (7 / 30 / 90 days, custom) is shared by the first two.

### 3.2 Conventions to keep

These already exist and the new pages should use them rather than invent: `ErrorState` for a section that failed to load, `ErrorMessage` for a submitted form, `EmptyState` for no data, skeletons that match the final layout, the status colours in `features/orders/status.ts`, reduced-motion respected, and the single admin guard in the layout. Every analytics number carries its period and says "UTC".

### 3.3 Charts: a decision for you

Adding a charting library is a new dependency, which this project has avoided unless asked.
- **Option A, no dependency (recommended to start):** KPI cards, CSS/SVG bar charts and sparklines written by hand. Enough for one daily series and a reasons breakdown, and nothing to maintain.
- **Option B, `recharts`** (what shadcn's chart components are built on): richer tooltips, axes and legends; adds a dependency and a noticeable bundle cost to the admin app only (which a separate admin app contains).

I would build the dashboard with A and revisit B only if the charts need interaction A cannot give.

## 4. Splitting the frontend into a monorepo

The decision recorded earlier was to defer the split until admin outgrew one section. Three sections, a dashboard and its own endpoints is that point. The split also pays for itself here: the dashboard's charts and tables do not belong in the shop's bundle or its layout.

### 4.1 Target layout

```
taskmanager-web/
  package.json            (npm workspaces: apps/*, packages/*)
  apps/web/               the shop (what is the repository today)
  apps/admin/             the admin app (dashboard, orders, products)
  packages/ui/            components/ui, the cn helper, the theme tokens (globals)
  packages/api/           the API client, error helper, feature API files and types
  packages/auth/          the auth store, JWT helper, useRequireAuth, login/logout hooks
  packages/config/        shared tsconfig and eslint config
```

npm workspaces, no new tool. Turborepo is optional and only worth adding once builds are slow.

### 4.2 Two things that make this smaller than it looks

- **The admin app needs its own login, and that is fine.** `localStorage` is per origin, so a second origin (port 3200 locally) does not see the shop's session. For an admin app that is a feature, not a problem: admins sign in to it directly. So the cookie-versus-localStorage decision that blocks refresh tokens does **not** block the split.
- **The backend change is one line:** the gateway's CORS allow-list (`Cors:AllowedOrigins`, today only `http://localhost:3100`) gains the admin origin. Locally that is 3200; on Azure, the admin app's address.

### 4.3 Extraction order

Each step leaves both the build and the e2e tests green, and is its own commit:

1. **Workspace skeleton.** Move the app to `apps/web` with `git mv` (history kept), add the root `package.json` with workspaces, fix the paths that assume a repository root: `tsconfig` paths, `components.json`, `playwright.config.ts`, Husky and lint-staged (they move to the root), the CI workflow's working directory, `.env.local`. Nothing is shared yet.
2. **`packages/ui`.** Move `components/ui`, `cn`, the theme tokens. Tailwind v4 has to be told to scan the package (`@source`); shadcn's `components.json` is set per package. The app imports from `@taskmanager/ui`.
3. **`packages/api`.** `lib/api-client.ts` imports the auth store and the query client, so it cannot move as is. It becomes a factory, `createApiClient({ baseURL, getToken, onUnauthorized })`, and each app supplies its own. The feature `api.ts` files and DTO types move with it.
4. **`packages/auth`.** The store, `jwt.ts`, `useRequireAuth`, the login/logout hooks, parameterized by the routes they redirect to. `useLogout` keeps clearing the cart through a callback the shop supplies, so the package does not depend on the shop's cart.
5. **`apps/admin`.** A new Next app on port 3200 with its own layout, login page and guard, receiving `app/admin/*`. The sidebar and `NAV` array move over. The shop's `/account` link to admin becomes a link to the admin app's address (an environment variable).
6. **Remove admin from the shop.** Delete `app/admin`; the lint boundary rule in `eslint.config.mjs` becomes a plain package boundary and is deleted.
7. **CI.** One workflow with path filters (a change to `packages/*` builds both apps; to one app, only it), and the e2e job starts both apps.

### 4.4 Risks

- Tailwind v4 content scanning across packages is the most likely source of "styles missing" surprises. Step 2 should include a screenshot comparison of the shop before and after.
- Two Next apps against one backend means two dev servers; the preview config and `npm run dev` need to start both (or a script per app).
- Duplicate copies of `react` or `next` in the workspace break hooks and types in confusing ways; the packages must declare them as peer dependencies.
- It is a large, mechanical diff. Doing it as the seven commits above, before building the dashboard, keeps it reviewable and keeps feature work out of it.

## 5. Sequence and estimates

| Step | What | Rough size |
|---|---|---|
| 1 | Backend: `ResolvedAtUtc` and the indexes (one migration), tests | about 1 day |
| 2 | Backend: summary, timeseries, cancellations, pending, orders list, top products, tests, load check | about 2 days |
| 3 | Monorepo extraction, steps 1 to 4.3.4 (workspace, ui, api, auth) | about 1 to 2 days |
| 4 | Admin app and moving the existing products page | about 1 day |
| 5 | Dashboard section (KPIs, chart, cancellations, stuck list, low stock) | about 2 days |
| 6 | Orders section (table, filters, detail) | about 1 to 2 days |
| 7 | CI changes, docs, Azure routing for the second app | about 1 day |

Steps 1 and 2 (backend) and step 3 (extraction) are independent and can happen in either order or alternately; 4 needs 3; 5 and 6 need 2 and 4.

**Suggested first slice**, because it is small and proves the plumbing end to end: step 1, then one endpoint (`summary`), then the Dashboard showing just the KPI cards in the existing single-app admin layout. The extraction can happen under a working dashboard.

## 6. Decisions (settled 2026-10-11)

1. **Charts:** no dependency first. Hand-written KPI cards and CSS/SVG charts; `recharts` only if they need interaction that cannot be built that way.
2. **Order of work:** the thin vertical slice: the migration, then `summary`, then the Dashboard's KPI cards. The remaining endpoints and sections follow.
3. **Revenue:** the sum of quantity x unit price over **confirmed** orders at the price charged, with no tax, shipping, discounts or refunds, because none exist here. An order belongs to the period it was **created** in (not resolved in): `ResolvedAtUtc` is null for older orders, so it cannot be the key yet. Pending orders are not revenue yet; cancelled ones never are. The cancellation rate is cancelled / (confirmed + cancelled), leaving pending out.
4. **Admin app on Azure:** a second Container App behind the same gateway, one route per app. Does not block local work; part of monorepo step 7.
5. **Order detail:** shows the user id, and looks the customer up (`GET /users/{id}`, admin-only, one call per order opened) to show the name. If the lookup fails the id is still shown. The list shows ids only, so opening the Orders list costs no user lookups.

## 7. Not in scope

Exports (CSV), scheduled reports, alerts on the numbers, per-customer analytics, refunds and returns, multi-currency, real-time push updates (the page polls or refreshes), and anything needing data the system does not collect (traffic, abandoned carts).
