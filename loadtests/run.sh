#!/usr/bin/env bash
# Runs one performance or resilience test against the LOCAL docker-compose stack.
#
#   loadtests/run.sh <profile> [options]
#
# Profiles
#   load     can the system handle the traffic we expect?
#   stress   what happens when we exceed its capacity?
#   spike    what happens when traffic suddenly jumps?
#   soak     what happens after running under load for a long time?
#   chaos    what happens when a dependency fails?   (needs --fault, see --list)
#
# Options
#   --fault <name>       chaos only: which dependency to break (see --list)
#   --vus <n>            concurrent shoppers (load, soak, chaos)
#   --minutes <n>        soak length in minutes
#   --env NAME=VALUE     pass any other setting through to the k6 script (repeatable);
#                        the full list is in loadtests/README.md
#   --no-override        keep the 5-minute checkout timeout instead of the 30s test one
#   --no-invariants      skip the post-run database checks
#   --no-preflight       do not wait for a recently opened circuit breaker to close
#   --list               show the profiles and faults
#
# It only ever targets the local stack. To load test a deployed environment, run the
# k6 scripts directly - see loadtests/README.md - so that is always a deliberate act.
#
# Exit code: 0 all good; 1 a k6 threshold failed; 2 an invariant was violated; 3 the
# run itself could not be carried out.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
K6_IMAGE="${K6_IMAGE:-grafana/k6:2.3.0}"
GATEWAY_URL="http://localhost:8000"
ORDER_HEALTH_URL="http://localhost:8085/health"

# shellcheck source=scripts/faults.sh
source "$SCRIPT_DIR/scripts/faults.sh"

info() { echo "==> $*"; }
die() { echo "error: $*" >&2; exit 3; }
usage() { sed -n '2,/^set -uo/p' "${BASH_SOURCE[0]}" | sed '$d' | sed 's/^# \{0,1\}//'; }

# ── arguments ────────────────────────────────────────────────────────────────

[[ $# -ge 1 ]] || { usage; exit 3; }
case "$1" in
  -h|--help) usage; exit 0 ;;
  --list) echo "profiles: load stress spike soak chaos"; echo "faults:"; fault_names | sed 's/^/  /'; exit 0 ;;
esac

PROFILE="$1"; shift
case "$PROFILE" in load|stress|spike|soak|chaos) ;; *) die "unknown profile '$PROFILE' (see --list)";; esac

FAULT="" OVERRIDE=true INVARIANTS=true PREFLIGHT=true
EXTRA_ENV=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fault)          FAULT="${2:?--fault needs a name}"; shift 2 ;;
    --vus)            EXTRA_ENV+=("VUS=${2:?--vus needs a number}"); shift 2 ;;
    --minutes)        EXTRA_ENV+=("SOAK_MINUTES=${2:?--minutes needs a number}"); shift 2 ;;
    --env)            EXTRA_ENV+=("${2:?--env needs NAME=VALUE}"); shift 2 ;;
    --no-override)    OVERRIDE=false; shift ;;
    --no-invariants)  INVARIANTS=false; shift ;;
    --no-preflight)   PREFLIGHT=false; shift ;;
    *) die "unknown option '$1'" ;;
  esac
done

if [[ "$PROFILE" == "chaos" ]]; then
  [[ -n "$FAULT" ]] || die "chaos needs --fault <name>; see --list"
  fault_known "$FAULT" || die "unknown fault '$FAULT'; see --list"
elif [[ -n "$FAULT" ]]; then
  die "--fault only applies to the chaos profile"
fi

# Unless told otherwise, give the fault the settle time it needs (see faults.sh).
if [[ "$PROFILE" == "chaos" && -z "${SETTLE_S:-}" ]]; then
  export SETTLE_S="$(fault_settle "$FAULT")"
  export RECOVERY_S="${RECOVERY_S:-$((SETTLE_S + 60))}"
fi

# Settings the k6 scripts read. Anything set in the environment or via --env is
# forwarded; nothing else is, so a stray variable cannot change a run.
PASS_ENV=(PRODUCTS VUS HOLD RAMP STEPS STEP_S MAX_VUS BASE_VUS SPIKE_FACTOR HOLD_S SOAK_MINUTES
  BASELINE_S FAULT_S RECOVERY_S SETTLE_S USERS LOADTEST_STOCK CHECKOUT_WAIT_S MIX_BROWSE
  MIX_BASKET P95_BROWSE_MS P95_DETAIL_MS P95_ORDER_CREATE_MS P95_CHECKOUT_MS
  MAX_FAILED_RATE MIN_CONFIRMED_RATE DASHBOARD_VUS)

for pair in "${EXTRA_ENV[@]:-}"; do
  [[ -z "$pair" ]] && continue
  [[ "$pair" == *=* ]] || die "--env expects NAME=VALUE, got '$pair'"
  export "${pair%%=*}=${pair#*=}"
done

# ── preflight ────────────────────────────────────────────────────────────────

command -v docker >/dev/null || die "docker is not available in this shell (run this from WSL or Linux)"
command -v curl >/dev/null || die "curl is required"

curl -sf --max-time 5 "$GATEWAY_URL/products?pageSize=1" >/dev/null \
  || die "the stack is not answering at $GATEWAY_URL. Start it: cd deploy && docker compose up -d"

NET="$(docker inspect taskflow-gateway --format '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}' 2>/dev/null)"
[[ -n "$NET" ]] || die "could not find the docker network of taskflow-gateway"

# Admin credentials let the run create its own product with plenty of stock. They are
# read from the same git-ignored file docker-compose uses, only ever passed to the
# container by name (never on a command line), and never printed.
env_value() { grep -E "^$1=" "$ROOT/deploy/.env" 2>/dev/null | head -1 | cut -d= -f2- | tr -d '\r' | sed -e 's/^"//' -e 's/"$//'; }
ADMIN_EMAIL="$(env_value ADMIN_EMAIL)"; ADMIN_PASSWORD="$(env_value ADMIN_PASSWORD)"
export ADMIN_EMAIL ADMIN_PASSWORD
if [[ -z "$ADMIN_EMAIL" || -z "$ADMIN_PASSWORD" ]]; then
  echo "error: ADMIN_EMAIL and ADMIN_PASSWORD are required in deploy/.env: creating the run's products and reading exact stock are admin-only" >&2
  exit 3
fi

RUN_ID="$(date +%Y%m%d-%H%M%S)"
RESULT_NAME="${PROFILE}${FAULT:+-$FAULT}-$RUN_ID"
RESULTS="$SCRIPT_DIR/results/$RESULT_NAME"
mkdir -p "$RESULTS"
START_TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

COMPOSE=(docker compose -f "$ROOT/deploy/docker-compose.yml")
COMPOSE_TEST=(docker compose -f "$ROOT/deploy/docker-compose.yml" -f "$SCRIPT_DIR/docker-compose.loadtest.yml")

# ── the stack: short timeout for the run, restored afterwards ────────────────

OVERRIDE_APPLIED=false
SAMPLER_PID="" CONN_SAMPLER_PID="" TAIL_PID="" K6_PID="" HEARTBEAT_PID=""

wait_for_url() {
  local url="$1" seconds="$2" waited=0
  until curl -sf --max-time 3 "$url" >/dev/null 2>&1; do
    sleep 2; waited=$((waited + 2))
    [[ "$waited" -ge "$seconds" ]] && return 1
  done
  # Explicit: an `until` loop's status is that of the last command in its body, which
  # here is the failed `[[ ]]` above - so without this it would report failure exactly
  # when the wait SUCCEEDED, but only if it had to wait at least once.
  return 0
}

restore_stack() {
  trap - EXIT INT TERM
  [[ -n "$TAIL_PID" ]] && kill "$TAIL_PID" 2>/dev/null
  [[ -n "$SAMPLER_PID" ]] && kill "$SAMPLER_PID" 2>/dev/null
  [[ -n "$CONN_SAMPLER_PID" ]] && kill "$CONN_SAMPLER_PID" 2>/dev/null
  [[ -n "$HEARTBEAT_PID" ]] && kill "$HEARTBEAT_PID" 2>/dev/null
  docker rm -f loadtest-k6 >/dev/null 2>&1
  fault_restore_all
  if $OVERRIDE_APPLIED; then
    info "restoring order-api to its normal configuration"
    "${COMPOSE[@]}" up -d order-api >/dev/null 2>&1
  fi
}
trap restore_stack EXIT INT TERM

if $OVERRIDE; then
  info "applying the 30s checkout timeout for the run (restored afterwards)"
  "${COMPOSE_TEST[@]}" up -d order-api >/dev/null 2>&1 || die "could not apply the test configuration"
  OVERRIDE_APPLIED=true
  wait_for_url "$ORDER_HEALTH_URL" 90 || die "order-api did not become healthy"
fi

# ── preflight ────────────────────────────────────────────────────────────────

error_total() {
  docker exec taskflow-rabbitmq rabbitmqctl list_queues name messages --no-table-headers 2>/dev/null \
    | awk '$1 ~ /_error$/ { s += $2 } END { print s + 0 }'
}

# The services trip a circuit breaker after a burst of failures and keep it open for
# five minutes. A run that starts while one is open measures that leftover, not what
# it was meant to, and the failure looks like the new run's. The breaker is in-process
# state with no API, so the only outside sign is its stack frames in the log: wait
# until none have appeared for five minutes. This only guards the START of a run; a
# breaker that trips DURING one is a result and is left alone.
wait_for_breakers_closed() {
  local waited=0 recent container
  while :; do
    recent=0
    for container in taskflow-inventory-api taskflow-order-api taskflow-users-api; do
      recent=$((recent + $(docker logs --since 5m "$container" 2>&1 | grep -c 'CircuitBreaker')))
    done
    [[ "$recent" -eq 0 ]] && return 0
    [[ "$waited" -eq 0 ]] && info "a circuit breaker opened in the last 5 minutes; waiting for it to close so it cannot spoil this run"
    sleep 20; waited=$((waited + 20))
    if [[ "$waited" -ge 420 ]]; then
      echo "warning: circuit-breaker activity still showing after 7 minutes; continuing anyway" >&2
      return 0
    fi
  done
}

$PREFLIGHT && wait_for_breakers_closed
# Taken after the wait, so it reflects the queue as this run actually begins.
ERRORS_BEFORE="$(error_total)"
[[ "$ERRORS_BEFORE" -gt 0 ]] && echo "note: $ERRORS_BEFORE message(s) already sit in _error queues; only NEW ones will fail this run" >&2

# ── resource sampling ────────────────────────────────────────────────────────

start_sampler() {
  (
    while :; do
      docker stats --no-stream --format '{{.Name}},{{.MemUsage}},{{.CPUPerc}}' 2>/dev/null \
        | awk -F, -v t="$(date +%s)" '$1 ~ /^taskflow-/ { print t "," $0 }' >> "$RESULTS/stats.csv"
      sleep 15
    done
  ) &
  SAMPLER_PID=$!
}

# A container whose memory only ever grows is the classic soak-test finding. The first
# 40% of the run is treated as warm-up and ignored: a .NET service that has just started
# climbs for minutes (the heap grows before the collector first compacts it), and a first
# version of this that compared the start of the run with the end flagged exactly that as
# a leak. What is compared is the window at 40-50% of the run against the last tenth: a
# leak is still climbing there, warm-up has already levelled off or fallen. Only growth
# that is large in relative terms and not a few MiB of noise is flagged, and it is a
# warning, not a failure - a short run cannot tell a slow leak from a slow plateau.
memory_report() {
  [[ -s "$RESULTS/stats.csv" ]] || return 0
  echo
  echo "Container memory, mid-run (40-50%) vs the last tenth (first 40% ignored as warm-up):"
  awk -F, '
    function mib(s,   p, n, u) {
      split(s, p, " "); n = p[1]; u = p[1]
      sub(/[A-Za-z]+$/, "", n); sub(/^[0-9.]+/, "", u)
      if (u == "KiB") return n / 1024
      if (u == "GiB") return n * 1024
      if (u == "B")   return n / 1048576
      return n
    }
    { c[$2]++; m[$2, c[$2]] = mib($3) }
    END {
      for (name in c) {
        n = c[name]
        lo = int(n * 0.4); hi = int(n * 0.5); if (lo < 1) lo = 1; if (hi < lo) hi = lo
        k = int(n / 10); if (k < 1) k = 1
        mid = 0; last = 0
        for (i = lo; i <= hi; i++) mid += m[name, i]
        mid /= (hi - lo + 1)
        for (i = n - k + 1; i <= n; i++) last += m[name, i]
        last /= k
        growth = mid > 0 ? (last - mid) / mid * 100 : 0
        flag = (growth > 50 && last - mid > 50) ? "  <-- WARN: still growing" : ""
        printf "  %-30s %8.0f MiB -> %8.0f MiB  (%+5.0f%%)%s\n", name, mid, last, growth, flag
      }
    }' "$RESULTS/stats.csv" | sort
}

# Connections per database against its max_connections, every few seconds. An overloaded
# service shows up here first: the order database ran out of connections in a stress run
# while every HTTP request still succeeded, so nothing in the k6 numbers said so. Queried as
# the superuser, whose reserved slots still answer when ordinary clients are refused; a
# sample that fails anyway is recorded as refused (-1) rather than skipped.
DB_SAMPLE=(order:orderflow inventory:inventoryflow catalog:catalogflow users:userflow)

start_conn_sampler() {
  (
    while :; do
      t="$(date +%s)"
      for pair in "${DB_SAMPLE[@]}"; do
        c="${pair%%:*}"; d="${pair##*:}"
        row="$(docker exec "taskflow-postgres-$c" psql -U postgres -d "$d" -tAF, -c           "select count(*), count(*) filter (where state = 'active'), count(*) filter (where state like 'idle in transaction%'), current_setting('max_connections') from pg_stat_activity where datname = '$d'" 2>/dev/null)"
        echo "$t,$c,${row:--1,-1,-1,-1}" >> "$RESULTS/connections.csv"
      done
      sleep 3
    done
  ) &
  CONN_SAMPLER_PID=$!
}

# Peak connections per database, as a share of its limit, and how long it spent near it.
# Running out is the failure; coming close is the warning.
connections_report() {
  [[ -s "$RESULTS/connections.csv" ]] || return 0
  echo
  echo "Database connections (peak vs max_connections; samples every 3 s):"
  awk -F, '
    { db = $2; n[db]++
      if ($3 < 0) { refused[db]++; next }
      if ($3 > peak[db]) peak[db] = $3
      if ($4 > act[db]) act[db] = $4
      if ($5 > itx[db]) itx[db] = $5
      max[db] = $6
      if ($3 >= $6 * 0.9) near[db]++ }
    END {
      for (db in n) {
        pct = max[db] > 0 ? peak[db] / max[db] * 100 : 0
        flag = (refused[db] > 0) ? "  <-- REFUSED " refused[db] " sample(s)" : (pct >= 90 ? "  <-- WARN: within 10% of the limit" : "")
        printf "  %-10s peak %3d of %3d (%3.0f%%)   peak active %3d   peak idle-in-transaction %3d   near limit %3d s%s\n", db, peak[db], max[db], pct, act[db], itx[db], near[db] * 3, flag
      }
    }' "$RESULTS/connections.csv" | sort
}

# ── run k6 ───────────────────────────────────────────────────────────────────

K6_ENV=(-e BASE_URL=http://gateway:8080 -e "RUN_ID=$RUN_ID" -e "RESULT_NAME=$RESULT_NAME")
for name in "${PASS_ENV[@]}"; do
  [[ -n "${!name+x}" ]] && K6_ENV+=(-e "$name=${!name}")
done
[[ -n "$FAULT" ]] && K6_ENV+=(-e "FAULT=$FAULT")

run_k6() {
  docker run --rm --name loadtest-k6 --user "$(id -u):$(id -g)" --network "$NET" \
    -v "$SCRIPT_DIR/k6:/scripts:ro" -v "$RESULTS:/results" \
    "${K6_ENV[@]}" -e ADMIN_EMAIL -e ADMIN_PASSWORD \
    "$K6_IMAGE" run --quiet "/scripts/$1.js"
}

info "profile: $PROFILE${FAULT:+ ($FAULT)}   k6: $K6_IMAGE   results: ${RESULTS#"$ROOT/"}"
start_sampler
start_conn_sampler

# k6 runs with --quiet (its per-second progress lines would fill the log on a long run),
# so say something now and then so a soak does not look hung.
(
  minutes=0
  while sleep 60; do
    minutes=$((minutes + 1))
    echo "    ... still running (${minutes} min)"
  done
) &
HEARTBEAT_PID=$!

K6_RC=0
if [[ "$PROFILE" == "chaos" ]]; then
  BASELINE_S="${BASELINE_S:-45}"; FAULT_S="${FAULT_S:-60}"

  run_k6 chaos > "$RESULTS/k6.log" 2>&1 &
  K6_PID=$!
  tail -n +1 -f "$RESULTS/k6.log" &
  TAIL_PID=$!

  # k6 needs a minute or two to register users and create its product before traffic
  # starts, so the fault timeline is measured from the moment traffic actually begins.
  waited=0
  until grep -q LOADTEST_TRAFFIC_STARTED "$RESULTS/k6.log" 2>/dev/null; do
    kill -0 "$K6_PID" 2>/dev/null || { wait "$K6_PID"; K6_RC=$?; break; }
    sleep 1; waited=$((waited + 1))
    [[ "$waited" -gt 900 ]] && { echo "k6 never started sending traffic" >&2; break; }
  done

  if kill -0 "$K6_PID" 2>/dev/null; then
    info "traffic started; baseline for ${BASELINE_S}s"
    sleep "$BASELINE_S"
    info "FAULT: $FAULT  (${FAULT_S}s)"
    fault_inject "$FAULT"
    sleep "$FAULT_S"
    info "restoring $FAULT"
    fault_restore "$FAULT"
    wait "$K6_PID"; K6_RC=$?
  fi
  kill "$TAIL_PID" 2>/dev/null; TAIL_PID=""
else
  run_k6 "$PROFILE" 2>&1 | tee "$RESULTS/k6.log"
  K6_RC=${PIPESTATUS[0]}
fi

kill "$SAMPLER_PID" "$HEARTBEAT_PID" 2>/dev/null; SAMPLER_PID=""; HEARTBEAT_PID=""

# ── verdict ──────────────────────────────────────────────────────────────────

# k6 exits 99 when a threshold failed; anything else non-zero means it did not run.
[[ "$K6_RC" -ne 0 && "$K6_RC" -ne 99 ]] && { echo "k6 exited with $K6_RC before completing; see ${RESULTS#"$ROOT/"}/k6.log" >&2; exit 3; }

VERDICT_FAILS="$(grep -h LOADTEST_VERDICT "$RESULTS/k6.log" | grep -c '=FAIL' || true)"

INV_RC=0
if $INVARIANTS; then
  # One "LOADTEST_PRODUCT <id> <initial stock>" line per product the run used, joined
  # as id:stock,id:stock for the stock-conservation check.
  PRODUCTS_ARG="$(sed -n 's/.*LOADTEST_PRODUCT \([0-9a-fA-F-]\{36\}\) \([0-9]\{1,\}\).*/\1:\2/p' "$RESULTS/k6.log" | paste -sd, -)"

  # Long enough for the timeout to fire, late reservations to be released and the
  # outbox to drain - the slow parts of the system catching up after the load stops.
  # Stress can leave a backlog that takes minutes to drain (observed >150 s at 600 it/s).
  SETTLE_DEFAULT=150; [[ "$PROFILE" == "stress" ]] && SETTLE_DEFAULT=600
  SETTLE="${SETTLE_WAIT_S:-$SETTLE_DEFAULT}"
  info "checking invariants (waiting up to ${SETTLE}s for the system to settle)"
  "$SCRIPT_DIR/scripts/invariants.sh" --start "$START_TS" ${PRODUCTS_ARG:+--products "$PRODUCTS_ARG"} \
    --errors-before "$ERRORS_BEFORE" --wait "$SETTLE" | tee "$RESULTS/invariants.txt"
  INV_RC=${PIPESTATUS[0]}
fi

kill "$CONN_SAMPLER_PID" 2>/dev/null; CONN_SAMPLER_PID=""
connections_report | tee "$RESULTS/connections.txt"

[[ "$PROFILE" == "soak" ]] && memory_report | tee "$RESULTS/memory.txt"

echo
echo "Results: ${RESULTS#"$ROOT/"}"
echo "Test data was left in the stack. Remove it with: loadtests/scripts/cleanup.sh"

[[ "$INV_RC" -eq 1 ]] && exit 2
[[ "$K6_RC" -ne 0 || "$VERDICT_FAILS" -gt 0 ]] && exit 1
# Nothing wrong was found, but some invariant could not be checked: not a pass.
[[ "$INV_RC" -eq 4 ]] && exit 4
[[ "$INV_RC" -ne 0 ]] && exit 3
exit 0
