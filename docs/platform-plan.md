# Platform & observability plan

Evaluation of ten candidate technologies against what this repository actually has today. This document is the decision record for *whether* and *where* each one belongs.

**Status:** metrics, Prometheus, Grafana and the custom saga metrics (sections 1–4, phase 1) are **implemented** — see [observability.md](observability.md) for what actually shipped, which differs from the plan in two places: rate limiting was *not* included in phase 1, and Prometheus/Grafana start with the stack rather than sitting behind an `observability` profile (observability that must be opted into is observability nobody enables). Everything else below is still a proposal.

Current baseline as it was *before* phase 1, verified at the time:

- **Traces:** OpenTelemetry 1.18.0 with ASP.NET Core + HttpClient instrumentation, OTLP → Jaeger. `MassTransit` added as an activity source in the four bus services.
- **Logs:** Serilog to console, enriched with `Service` + `TraceId`/`SpanId`.
- **Metrics:** none. `AddObservability` calls `WithTracing` only — there is no `WithMetrics`, no meter, no exporter.
- **Rate limiting:** none. `System.Threading.RateLimiting` appears in `project.assets.json` only as a transitive MassTransit dependency; no `AddRateLimiter` call exists. (.NET 8's `Microsoft.AspNetCore.RateLimiting` is in the shared framework — no package reference needed.)
- **Tokens:** 1-hour HS256 access token, no refresh, no revocation, `jti` minted but never stored.
- **Correlation:** already solved *internally* by the OTel trace id.
- **CI:** build + six unit-test projects. `Saga.IntegrationTests` excluded. No CD, no image publishing.
- **Admin tooling:** none — Postgres/Redis are reachable only via published ports.

---

## Verdict summary

| Technology | Verdict | Why |
|---|---|---|
| Metrics (OTel `WithMetrics`) | **Do now** | The one genuine blind spot; everything else on this list depends on it |
| Prometheus | **Do now** | Needed as the metrics store; scrape-based is the right fit for compose |
| Grafana | **Do now** | Single pane over Prometheus + Jaeger; replaces nothing, adds correlation |
| Rate limiting | **Do now**, narrowly | Closes a confirmed brute-force/DoS gap on `POST /users/login` |
| pgAdmin + Redis Insight | **Do now** (dev profile only) | Near-zero cost, real day-to-day payoff, must never ship to prod |
| CI improvements | **Do next** | Consolidate test steps, finally run the saga integration test |
| Refresh tokens | **Do next** | Real gap, but a cross-repo change — needs a frontend decision first |
| Correlation IDs (ingress/egress) | **Do next**, small | Internal propagation already works; only the edges are missing |
| CD / deployment automation | **Skip for now** | There is no deployment target to deliver to |
| Dedicated metrics for the saga | **Do now**, alongside metrics | This is where metrics earn their keep in *this* system |

**A prioritisation caveat worth stating plainly:** none of this fixes the two highest-impact findings in [TODO.md](TODO.md) — the saga has no timeout (orders can strand in `Pending` with stock reserved forever) and order prices are client-supplied and unvalidated *(since fixed: see [services.md](services.md))*. Observability will *reveal* the first one faster; it won't fix either. If effort is limited, those two changes outrank this entire list. *(Update: the saga timeout has since been implemented — see [order-flow.md](order-flow.md) — with one known gap, recorded in [TODO.md](TODO.md); order prices remain unvalidated.)* The strongest argument for doing metrics first is that a stuck-saga gauge turns an invisible failure into a visible one while the real fix is being written.

---

## 1. Metrics — OpenTelemetry `WithMetrics`

**What it costs:** one `OpenTelemetry.Instrumentation.Runtime` package, a `WithMetrics` block in `ObservabilityExtensions`, and an exporter choice. The OTel packages and the `AddObservability` seam already exist, so this is a genuinely small change touching one shared file.

**Trade-off on exporter shape** — this is the real decision:

| Option | Pros | Cons |
|---|---|---|
| **A. Prometheus exporter in each service** (`OpenTelemetry.Exporter.Prometheus.AspNetCore`, `/metrics` endpoint, Prometheus scrapes) | Simplest topology, no extra hop, easy to debug by curling `/metrics`, Prometheus handles service discovery | Adds a scrapeable public endpoint per service (must be excluded from the gateway routes); traces go OTLP while metrics go Prometheus — two paths |
| **B. OTLP for metrics too, into an OTel Collector** that fans out to Prometheus + Jaeger | One export path for everything, one place to configure sampling/enrichment, swappable backends | A new component to run and configure; more moving parts in compose; harder to debug when nothing shows up |

**Decision: Option A.** The project already exports traces directly to Jaeger without a collector, and introducing one now adds a component whose only current job is fan-out. Option B becomes correct the moment there's a second backend or a real cluster — note it as the migration path rather than pre-building it.

**Instrumentation to enable:** ASP.NET Core (request duration/count — gives RED metrics for free), HttpClient, Runtime (GC, thread pool), and `Npgsql`'s built-in meter. MassTransit 8.3 exposes its own meters (consume duration, message counts) — those come for free once metrics are on.

## 2. Custom metrics that actually matter here

Generic RED dashboards are the cheap part. The metrics that pay for this work map directly onto the confirmed risks:

| Metric | Type | Answers |
|---|---|---|
| `order_saga_active` | gauge | **How many sagas are stuck?** This plan assumed no saga timeout, in which case this was the only signal of an order stranded in `AwaitingStockReservation`; the timeout has since been added, so it now shows checkouts in flight and any the sweeper has not yet resolved. |
| `order_saga_duration` | histogram | Normal checkout latency, so "stuck" has a threshold |
| `orders_finalized_total{outcome}` | counter | Confirm vs cancel ratio — a spike in cancels means stock or catalog trouble |
| `stock_reservation_failed_total{reason}` | counter | Separates "insufficient stock" from "no inventory record" (a data-integrity problem, not a business one) |
| `outbox_backlog` | gauge | Outbox rows pending delivery; a rising value means messages are committed but not reaching the broker |
| RabbitMQ `_error` queue depth | from RabbitMQ exporter | **Currently invisible.** Messages that exhaust retry are silently parked, and that is precisely the condition that strands a saga. |

The last one needs RabbitMQ's `rabbitmq_prometheus` plugin (built into the `rabbitmq:3-management` image, enable via `rabbitmq-plugins enable`) rather than app instrumentation. Postgres and Redis exporters are available too, but are lower value until there's a production-shaped load.

## 3. Prometheus

**Fit:** good. Pull-based scraping suits a fixed compose topology where every target is a known DNS name. Retention on a local volume, a 15s scrape interval, and no alertmanager to start with.

**Trade-off to be aware of:** Prometheus is dimensional-but-not-high-cardinality. `OrderId`/`UserId` must never become a label — that's what traces are for. The metric/trace division of labour: *metrics answer "how many, how often, how slow"; traces answer "what happened to this one order."* Keeping that line clean is the main discipline this adds.

## 4. Grafana

**Fit:** good, with one specific payoff beyond dashboards — configure **both** Prometheus and the existing Jaeger as datasources, and a spike on a latency panel becomes a click through to the actual traces. That closes the loop the current setup can't: today Jaeger can show you one slow checkout, but nothing tells you checkouts got slow.

**Trade-off:** dashboards are real maintenance, and hand-built ones rot. Provision them as code (`grafana/provisioning/`, committed) rather than clicking them together in the UI — otherwise they live only in a container volume and vanish. Start with two dashboards, not ten: one service-health (RED per service) and one checkout-flow (the saga metrics above).

Grafana Loki for logs is deliberately **out of scope** for now — Serilog console output plus `docker compose logs` is adequate at this size, and adding Loki means also adding a Serilog sink and a log pipeline. Revisit when console logs stop being greppable.

## 5. Rate limiting

Two viable placements, and the answer is "both, for different reasons":

| Placement | Good for | Limitation |
|---|---|---|
| **Gateway** (YARP 2.3 supports `RateLimiterPolicy` per route) | Blanket protection, one config file, keeps junk traffic off the services | **Bypassable in the current topology** — compose publishes every service port (8080–8085) directly, so the gateway is not the only path. In-memory, so per-instance. |
| **UserService**, on `POST /users/login` | The actual attack being mitigated: credential stuffing, plus the PBKDF2-at-100k-iterations CPU amplification that makes each attempt expensive for *us* | Only covers that one service |

**Decision:** put a strict partitioned limiter on `POST /users/login` (and `POST /users`) in UserService — that's a security control and belongs with the thing it protects. Add a generous gateway-wide limiter as defence in depth. .NET 8's built-in `AddRateLimiter` covers both; **no new package is needed**, which makes this the cheapest item on the list.

**Design trade-offs:**
- **Partition key:** IP alone is defeated by a botnet and punishes users behind shared NAT. Partition login attempts by IP *and* by submitted email (a sliding window per account), so one account can't be ground down regardless of source.
- **In-memory vs distributed:** in-memory limits are per-instance, so N replicas means N× the intended limit. Correct for now (single instance each); Redis-backed limiting is the scale-out answer, and Redis is already in the stack for BasketService — but adding a Redis dependency to UserService to solve a problem it doesn't yet have is premature.
- **Response:** return `429` with a `Retry-After` header. The frontend repo needs to handle that in its Axios interceptor alongside the existing 401 path.
- **Don't rate-limit `/health`** or the future `/metrics` — Prometheus scraping at 15s would trip a naive global limiter.

## 6. Refresh tokens

**The gap is real:** a 1-hour access token with no revocation means a leaked token is valid for its full hour and a "log out everywhere" or "disable this account" action is impossible. The `jti` claim is already minted — it just isn't persisted or checked.

**What it actually requires** (this is not a small change):
- A `RefreshTokens` table in UserService: hashed token, user id, expiry, `RevokedAtUtc`, and a rotation chain reference. **Store a hash, never the token itself** — the same reasoning as password storage.
- `POST /users/refresh` and `POST /users/logout` endpoints.
- Rotation on every use, with **reuse detection**: if an already-rotated token is presented again, that indicates theft — revoke the whole chain for that user.
- Shorten the access token to ~15 minutes, or the refresh token buys little.

**Trade-off, and the reason this is "next" not "now":** the biggest decision is where the frontend keeps the refresh token, and the frontend is now a **separate repo** (`taskmanager-web`). An `HttpOnly; Secure; SameSite` cookie is meaningfully safer than `localStorage` (immune to XSS exfiltration) but requires CSRF protection and commits the gateway to credentialed cross-origin cookies — it already sets `AllowCredentials()`, so that part is in place. `localStorage` is easier and is what an Axios interceptor expects, but any XSS becomes a full account takeover. **This should be agreed across both repos before either side starts.**

Worth noting: the access token stays stateless and self-validating in all six services either way — only the refresh endpoint touches the database. That preserves the current architecture's main virtue. A revocation *list* checked on every request would not, and should be avoided.

## 7. Correlation IDs

**Mostly already solved, and worth not rebuilding.** The OTel trace id propagates HTTP → message → consumer via W3C `traceparent` and MassTransit's native activity source, and Serilog prints it on every line. A hand-rolled `X-Correlation-Id` header would duplicate this and drift from it.

The genuine gaps are only at the edges:
1. **Ingress:** a `traceparent` sent by the frontend is honoured by ASP.NET Core automatically, but nothing in the frontend sends one. Cross-repo item.
2. **Egress:** the trace id is never returned to the client. Without it, a user-reported problem can't be tied to a trace. Add the trace id to a response header, and include it in `ProblemDetails` responses from the shared `ApiExceptionHandler` pattern.

Item 2 is a handful of lines per service and is the highest value-to-effort item on this entire list. Note the mild information-disclosure trade-off — a trace id is an opaque random value, so exposing it leaks nothing about internals, but it does confirm that tracing exists.

## 8. pgAdmin and Redis Insight

**Verdict: yes, but fenced off.** Both are pure developer convenience with no application-code impact.

- **pgAdmin** genuinely helps here because there are **five separate Postgres instances** (`taskflow`, `userflow`, `catalogflow`, `inventoryflow`, `orderflow`). Pre-provision all five in a committed `servers.json` so nobody has to add connections by hand — that's most of the value. It also makes inspecting the `OutboxState`/`InboxState`/`OrderSagaStates` tables practical, which matters for debugging the saga.
- **Redis Insight** is lower value (one keyspace, one flat `basket:{userId}` key pattern) but costs nothing and beats `redis-cli` for inspecting the JSON blobs.

**Non-negotiable constraints:** put both behind a compose `profiles:` entry so `docker compose up` stays lean, and never let them into a production compose file or reachable network. pgAdmin's own login must come from `deploy/.env` like every other secret — do **not** add default credentials to a committed file. That would repeat the placeholder-credential problem already flagged in [TODO.md](TODO.md).

**Trade-off to acknowledge honestly:** four new containers (Prometheus, Grafana, pgAdmin, Redis Insight) on top of the eleven already in compose makes local startup noticeably heavier on a developer laptop. Compose profiles are what keep this tolerable — suggested split: default = app + infra, `observability` = Prometheus/Grafana, `tools` = pgAdmin/Redis Insight.

## 9. CI improvements

Concrete changes to `.github/workflows/ci.yml`, in value order:

1. **Run the saga integration test.** GitHub-hosted Ubuntu runners have Docker, so Testcontainers works. The flakiness noted in the workflow comment is real and partly documented in the test itself (the RabbitMQ auth-init race, handled with a fixed 5s delay) — but a test excluded from CI is a test that will silently rot, and it is currently the *only* automated coverage of the distributed workflow. Put it in a **separate job** so its slowness and any flakiness don't block the fast unit-test signal.
2. **Collapse the six per-project test steps into one** `dotnet test NovaCommerce.slnx` with the integration project filtered out by trait/category. Six near-identical steps is maintenance that grows with every new service.
3. **Validate migrations** — a step that fails if `dotnet ef migrations has-pending-model-changes` reports drift catches the common "changed the entity, forgot the migration" mistake. Given nothing applies migrations at startup, migration hygiene matters more here than usual.
4. **Cache NuGet** (`actions/setup-dotnet` with `cache: true`) — cheap wall-clock win.
5. **Build the Docker images** on PR. Six Dockerfiles are referenced by compose and are currently never exercised by CI, so a broken Dockerfile is only discovered locally.

## 10. CD — deliberately deferred

**Recommendation: stop at continuous *delivery* of images; do not build continuous deployment yet.** Pushing tagged images to GHCR on merge to `main` is useful and self-contained. Beyond that there is nothing to deploy *to*: the repository has `docker-compose.yml` and no Kubernetes manifests, Helm charts, Terraform, or target environment. Building a deployment pipeline before choosing a runtime means building it twice.

Two things should be settled *before* any CD work, because they shape it:

- **Migrations.** Nothing applies them at startup, so any deployment pipeline needs an explicit migration step ordered before the new version starts. This is a design decision (init container? one-shot job? `Database.Migrate()` on boot with its own concurrency hazard when replicas roll?) and it's currently unmade.
- **Secrets.** `deploy/.env` works locally. A real environment needs a secret store, and the committed placeholder `Jwt:Key` must fail startup outside Development rather than silently becoming the production signing key.

---

## Suggested sequence

| Phase | Work | Status |
|---|---|---|
| 1 | `WithMetrics` + Prometheus exporter; Prometheus + Grafana; RED dashboard; saga/outbox metrics + checkout dashboard; RabbitMQ Prometheus plugin and `_error`-queue panel; alert rules | **Done** — phases 1 and 2's observability work landed together, since the saga metrics are the reason the dashboards are worth having |
| 2 | Login rate limiter; trace id in response headers and `ProblemDetails`; pgAdmin + Redis Insight in a `tools` profile | **Done** |
| 3 | CI: single test step, integration-test job, migration-drift check | **Done** (image build deferred to phase 5) |
| 4 | Refresh tokens + revocation — after the cookie-vs-storage decision is agreed with `taskmanager-web` | Blocked on that decision |
| 5 | Image publishing to GHCR. Revisit real CD once a deployment target exists | Not started |

Order-price validation, the saga timeout and the post-timeout stock-reservation gap are all done; the open items that matter most are listed in [TODO.md](TODO.md).
