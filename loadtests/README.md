# Load, stress, spike, soak and resilience tests

Performance tests for the whole stack, written for [k6](https://k6.io), run from a Docker image (nothing to install), and checked afterwards against the databases and the broker.

| Profile | The question it answers | Shape of the load |
|---|---|---|
| `load` | Can the system handle the traffic we expect? | A steady number of concurrent shoppers |
| `stress` | What happens when we exceed its capacity? | Arrival rate stepped up until something gives, then back down |
| `spike` | What happens if traffic suddenly jumps? | Calm, then 10x within 10 seconds, then calm again |
| `soak` | What happens after running under load for hours? | Steady moderate load for a long time |
| `chaos` | What happens when a dependency fails? | Steady load while a dependency is stopped or frozen, then restored |

```bash
loadtests/run.sh load                                  # 20 shoppers, 3 minutes steady
loadtests/run.sh stress
loadtests/run.sh spike
loadtests/run.sh soak --minutes 120
loadtests/run.sh chaos --fault inventory-down
loadtests/run.sh --list                                # profiles and faults
loadtests/scripts/cleanup.sh                           # remove the data the runs created
```

Run these from WSL or Linux (`docker compose up -d` in `deploy/` first). They only ever target the **local** stack; see [Testing a deployed environment](#testing-a-deployed-environment).

## What a run does

1. Applies a **30-second checkout timeout** to `order-api` (default is 5 minutes) and restores it afterwards. Without this, no run can be judged until five minutes after the load stops.
2. **Preflight.** If a circuit breaker opened in the last five minutes it waits (up to seven) for it to close, because a run that starts while one is open measures that leftover, and the failure looks like the new run's. It also notes how many messages already sit in `_error` queues, so only new ones count. Skip with `--no-preflight`.
3. Starts k6 in a container on the compose network. Its setup registers a few throwaway users, logs each in once, and (given admin credentials from `deploy/.env`) creates `PRODUCTS` dedicated `loadtest-...` products (default 5) with a million units of stock each.
4. Generates traffic: mostly browsing, some basket use, some checkouts. A checkout is timed from placing the order until it leaves `Pending`, because the saga makes it asynchronous and the HTTP latency of `POST /orders` alone says nothing about it.
5. For `chaos`, breaks a dependency on a timeline, then restores it.
6. Waits for the system to settle and checks the **invariants** below.

Results go to `loadtests/results/<profile>-<timestamp>/` (git-ignored): `k6.log`, the k6 JSON, `invariants.txt`, `connections.csv` / `connections.txt` (each database's open connections against its `max_connections`, every 3 s, with a peak summary), and for soaks a memory sample. A `REFUSED` connection sample means the database could not be queried at all, usually because it was out of connections (it can also mean the host was overloaded).

Exit code: `0` all good, `1` a k6 threshold failed, `2` an invariant was violated, `3` the run could not be carried out, `4` **inconclusive**: nothing was found wrong, but an invariant could not be checked (for example a database out of connections), so nothing was proven either.

## The invariants

Latency and error rate say how the system felt. They say nothing about whether it stayed *correct*, and correctness is what overload and failure usually damage. After every run `scripts/invariants.sh` reads the databases and RabbitMQ directly (not through the API that was just being abused) and checks:

| Invariant | What violating it means |
|---|---|
| No order left `Pending` | An order was abandoned: neither confirmed nor cancelled |
| No saga still awaiting stock | The saga and its timeout both failed to finish the checkout |
| **Stock conserved**, per product: `initial - sold = final` | Positive difference: stock **vanished** (a reservation nothing released). Negative: stock was **created** (something released twice). Checked per product, because one losing a unit while another gains one would cancel out in a sum |
| Outbox drained | A committed message never reached the broker |
| No **new** messages in `_error` queues | A message exhausted its retries during this run and nothing consumes those queues, so it would otherwise be silent. Compared with what was already there at the start |

Each invariant is `PASS`, `FAIL`, or `????` (could not be queried). "I could not look" is reported as inconclusive, never as a violation.

The system is eventually consistent, so the check keeps retrying for up to 150 seconds (`SETTLE_WAIT_S`; 600 for `stress`, whose backlog can take minutes to drain) before declaring a failure. One that clears was just still in flight; one that persists is real.

## Reading the results

- **Thresholds are for this machine, not for production.** The defaults (`P95_BROWSE_MS=500` and so on, in `k6/lib/config.js`) describe a laptop running the whole stack *and* the load generator, sharing CPU. Use a run to compare before and after a change, or to find where something breaks relative to itself. Do not quote its numbers as capacity. Set thresholds from real traffic and SLOs before using a run to make a capacity claim.
- **`load`**: any failure means the system cannot carry expected load. What "expected" is, is yours to say: set `VUS` from real numbers (concurrent users is roughly requests per second times average session think-time).
- **`stress`**: read the by-step table. The report names the highest step that held (<2% failed, p95 <1s, at least 95% of checkouts confirmed) and the first that did not, with the reason. Checkouts matter because overload here is invisible at HTTP level: at ~600 iterations/s the order database ran out of connections (`max_connections`), every request still returned success, but only about half the checkouts resolved during the step and the backlog took over 150 s to drain. All orders were eventually confirmed and stock was conserved, so this is a capacity limit, not corruption. The arrival-rate executor keeps starting iterations whether or not the system keeps up, which is what real overload looks like; watch `DROPPED` iterations too. Then look at the invariants: a system that fails *cleanly* under overload and comes back is fine; one that corrupts data is not.
- **`spike`**: the pass criteria apply to the baseline and the recovery, not the spike. Failing during a 10x jump is expected; not returning to normal afterwards is the finding.
- **`soak`**: the report compares p95 in the first tenth of the run with the last tenth (`LOADTEST_VERDICT soak-latency-drift`) and lists memory per container. Memory is compared between the **40-50% mark and the last tenth**; the first 40% is ignored as warm-up. A first version compared the start with the end and flagged `order-api` as a +112% leak; its memory chart (93 MiB, up to 422, flat, then back to 280 after a garbage collection) was a runtime warming up, not a leak. Growth is a **warning**, not a failure: a short run cannot tell a slow leak from a slow plateau, so use `--minutes 120` or more for a real soak, and read `stats.csv` in the results folder when a warning appears.
- **`chaos`**: the fault phase is deliberately unjudged (errors are expected while a dependency is down). Read *which* endpoints failed and how, and check that the baseline and recovery phases pass and the invariants hold. Recovery is judged only after a per-fault settle time (30 s, 60 s for the broker): a RabbitMQ client that lost its broker reconnects on a **growing backoff**, so after a 45 s outage checkouts resume roughly 25-30 s after the broker is back. Judging at 20 s caught that tail and called it a failure. Checkouts that begin in one phase and end in another are reported separately and counted in neither, so a fault cannot be blamed on the baseline.

### Faults (`--fault`)

Two failure shapes behave differently. **down** stops the container, so connections are refused immediately. **hang** freezes it (`docker pause`): connections stay open and nothing answers, which is what a saturated or partitioned service looks like and is the case that exposes missing timeouts.

`inventory-down` `inventory-hang` `catalog-down` `basket-down` `rabbitmq-down` `rabbitmq-hang` `order-db-down` `order-db-hang` `inventory-db-down` `redis-down` `order-restart` (a cold restart under load) `gateway-down`

## Settings

Pass with `--env NAME=VALUE` (or export them). Defaults in parentheses.

| Setting | Applies to | Meaning |
|---|---|---|
| `VUS` (20; soak/chaos 10) | load, soak, chaos | Concurrent shoppers |
| `HOLD`, `RAMP` (3m, 1m) | load | Steady period; ramp up/down |
| `STEPS` (5,10,20,40,80,160), `STEP_S` (60), `MAX_VUS` (300) | stress | Iterations/second per step; seconds per step; VU ceiling. **At several hundred iterations/s raise `MAX_VUS` (1200 for 600/s)**: at 300 the generator runs out of VUs, drops iterations (the report says `DROPPED`) and the system never receives the load you asked for, which is how one run reported "every step held" while the same steps with more VUs exhausted the database |
| `BASE_VUS` (5), `SPIKE_FACTOR` (10), `HOLD_S` (120) | spike | Calm level, multiple, seconds at the peak |
| `SOAK_MINUTES` (30) | soak | Length (also `--minutes`) |
| `BASELINE_S` (45), `FAULT_S` (60), `RECOVERY_S` (settle + 60), `SETTLE_S` (30; broker faults 60) | chaos | Seconds per phase; unjudged period after the fault |
| `USERS` (10) | all | Users logged in once and shared between VUs |
| `PRODUCTS` (5) | all | Products the load is spread across. **`PRODUCTS=1` puts every order on one inventory row** - a flash sale, and a deliberately harsh case (see below) |
| `MIX_BROWSE` (0.7), `MIX_BASKET` (0.2) | all | Traffic mix; the remainder is checkout |
| `DASHBOARD_VUS` (0) | load | Administrators with the dashboard open beside the shoppers, polling every admin analytics endpoint (90-day ranges) every couple of seconds. Needs the admin credentials. Compare a run with `DASHBOARD_VUS=0` to see whether analytics slow checkouts; the report adds an `admin analytics` latency line |
| `P95_BROWSE_MS`, `P95_DETAIL_MS`, `P95_ORDER_CREATE_MS`, `P95_CHECKOUT_MS`, `MAX_FAILED_RATE`, `MIN_CONFIRMED_RATE` | all | Pass/fail thresholds |
| `LOADTEST_STOCK` (1000000) | all | Stock for the dedicated product |
| `SETTLE_WAIT_S` (150) | run.sh | How long invariants may take to settle |

## Why it is built this way

- **Login cannot be load-tested, and the tests avoid it.** `POST /users/login` is limited to 10 per minute per IP on purpose (it is expensive by design and a brute-force target). So the tests log a small pool of users in once and share their tokens, retry on `429`, and the soak test re-logs-in each VU shortly before its one-hour token expires, staggered so they do not all hit the limit together.
- **Checkout needs its own stock.** It consumes real stock, so a run would otherwise exhaust a catalog product and turn into a test of the out-of-stock path. Given admin credentials, each run creates products with a million units each.
- **Spread the load, then concentrate it on purpose.** Every `ReserveStock` decrements one inventory row, so all orders on one product contend for that row. That is realistic (a flash sale) but it is the worst case, so load is spread over `PRODUCTS` products by default and `PRODUCTS=1` is the explicit hot-row test. The first chaos runs found this the hard way: concurrent reservations of one product failed with Postgres `40001` and most were parked in `_error` (40 at once: 37 failed). It is fixed (`docs/TODO.md`), and `Reservations_ForOneProduct_ArrivingTogether_AreAllApplied` in the integration suite now guards it far faster than a chaos run can.
- **Do not trust the first explanation.** That finding was first blamed on the single hot product. Spreading the load over five products failed just the same, and the hot-product run passed; the actual cause was the transaction isolation level and the retry schedule. A repeatable failure was needed before the cause could be told apart from the noise, which is why the concurrency case now lives in the integration tests as a deterministic reproduction.
- **Stress uses an arrival rate, not a VU count.** VU-based tests slow down together with the system, which hides the breaking point.
- **k6 cannot break containers, so `run.sh` does.** The k6 script only generates traffic and labels each request with the phase it fell in (`baseline`, `fault`, `settling`, `recovery`); `run.sh` injects and restores the fault on a timeline measured from the moment traffic actually starts.
- **Per-phase numbers need a trick.** k6 records a tagged sub-metric only when a threshold mentions it, so `k6/lib/phases.js` adds always-passing thresholds for each phase. They are excluded from the report's pass/fail list.

## Cost of a run

Runs create real rows in the local stack: throwaway users, `loadtest-...` products, and every order they placed. `scripts/cleanup.sh` removes exactly those, matched by name (`loadtest+%@example.com`, `loadtest-%`), and nothing else. The k6 results JSON deliberately omits the setup data, which holds those users' passwords and tokens.

## Testing a deployed environment

`run.sh` refuses anything but the local stack, so pointing a test at a real environment is always deliberate. To do it, run k6 directly against a target you own and are ready to overload:

```bash
docker run --rm -v "$PWD/loadtests/k6:/scripts:ro" -v "$PWD/loadtests/results:/results" \
  -e BASE_URL=https://your-apim-or-gateway -e ALLOW_REMOTE=true \
  -e ADMIN_EMAIL -e ADMIN_PASSWORD grafana/k6:2.3.0 run /scripts/load.js
```

That runs the traffic and thresholds only. The invariant checks and the fault injection read local containers, so they do not apply to a remote target; use the platform's own tooling (Azure Chaos Studio, the portal's dependency controls) for faults there, and query the databases directly for the invariants.

## Not covered yet

- **Network faults** (latency, packet loss) - only stop/pause/restart. Toxiproxy between the services would add them.
- **Live dashboards.** k6 can write to the Prometheus already in the stack (`-o experimental-prometheus-rw`, plus `--web.enable-remote-write-receiver` on Prometheus) so load and service metrics share a Grafana board. Not wired up.
- **CI.** These need the full stack and minutes to hours; they are run on demand, not per push.
- **Login endpoint performance**, by design (see above).
