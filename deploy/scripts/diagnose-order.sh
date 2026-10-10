#!/usr/bin/env bash
# Where is this order, and what is it waiting for?
#
#   deploy/scripts/diagnose-order.sh <order-id>
#   deploy/scripts/diagnose-order.sh --latest [N]     the N most recent orders (default 5)
#
# For an order that sits in "Processing" (Pending) this lines up everything that can hold it:
# the order and its saga, whether its messages left the outbox, how deep the queues are and
# whether anything is consuming them, what is parked in the _error queues, and what the two
# services have been logging about it. It only reads; it changes nothing.

set -uo pipefail

ORDER_DB="${ORDER_DB_CONTAINER:-taskflow-postgres-order}"
INVENTORY_DB="${INVENTORY_DB_CONTAINER:-taskflow-postgres-inventory}"
RABBIT="${RABBITMQ_CONTAINER:-taskflow-rabbitmq}"

die() { echo "error: $*" >&2; exit 1; }
orderdb() { docker exec "$ORDER_DB" psql -U postgres -d orderflow -tAF'|' -c "$1" 2>&1; }
invdb()   { docker exec "$INVENTORY_DB" psql -U postgres -d inventoryflow -tAF'|' -c "$1" 2>&1; }
section() { printf '\n== %s\n' "$1"; }

docker ps --format '{{.Names}}' | grep -qx "$ORDER_DB" || die "'$ORDER_DB' is not running (is the stack up?)"

if [[ "${1:-}" == "--latest" ]]; then
  n="${2:-5}"
  [[ "$n" =~ ^[0-9]+$ ]] || die "--latest takes a number"
  section "The $n most recent orders"
  orderdb "select \"Id\", \"Status\", \"CreatedAtUtc\"::timestamp(0), round(extract(epoch from (now() - \"CreatedAtUtc\")))::int as age_s from \"Orders\" order by \"CreatedAtUtc\" desc limit $n"
  echo
  echo "Run again with one of these ids for the detail."
  exit 0
fi

ID="${1:-}"
[[ "$ID" =~ ^[0-9a-fA-F-]{36}$ ]] || die "give an order id (36 characters), or --latest"
SHORT="${ID:0:8}"

section "Order"
row="$(orderdb "select \"Status\", \"UserId\", \"CreatedAtUtc\"::timestamp(0), round(extract(epoch from (now() - \"CreatedAtUtc\")))::int, coalesce(\"CancellationReason\",'') from \"Orders\" where \"Id\" = '$ID'")"
[[ -n "$row" ]] || { echo "no such order"; exit 1; }
IFS='|' read -r status user created age reason <<<"$row"
echo "status: $status   age: ${age}s   created: $created UTC   user: ${user:0:8}"
[[ -n "$reason" ]] && echo "reason: $reason"
echo "lines:"
orderdb "select \"ProductId\", \"Quantity\", \"UnitPrice\" from \"OrderItem\" where \"OrderId\" = '$ID'" | sed 's/^/  /'

section "Saga (the checkout)"
saga="$(orderdb "select \"CurrentState\", \"ResponseCount\", \"TotalItems\", \"HasFailure\", coalesce(\"FailureReason\",'') from \"OrderSagaStates\" where \"CorrelationId\" = '$ID'")"
if [[ -z "$saga" ]]; then
  echo "no saga row."
  case "$status" in
    Pending) echo "  -> The order is Pending and NOTHING is tracking it: its OrderSubmitted never started a saga."
             echo "     Look at the _error queues below; if OrderSubmitted is parked there, replay it:"
             echo "     deploy/scripts/replay-errors.sh OrderSagaState_error" ;;
    *)       echo "  -> Normal for a finished order: the saga row is deleted when the checkout completes." ;;
  esac
else
  IFS='|' read -r state responses total failed freason <<<"$saga"
  echo "state: $state   responses: $responses of $total   failed: $failed${freason:+   ($freason)}"
  [[ "$state" == "AwaitingStockReservation" ]] && echo "  -> Waiting for InventoryService to answer ($responses of $total so far). The sweeper cancels it after Checkout:Timeout."
fi

section "Outbox (messages committed but not yet sent to the broker)"
echo "order service:     $(orderdb 'select count(*) from "OutboxMessage"') waiting"
echo "inventory service: $(invdb 'select count(*) from "OutboxMessage"') waiting"

section "Queues (name, ready, consumers)"
docker exec "$RABBIT" rabbitmqctl list_queues name messages_ready consumers --no-table-headers 2>/dev/null \
  | awk '$1 ~ /^(OrderSagaState|ReserveStock|ReleaseStock)/ { printf "  %-34s ready %-6s consumers %s%s\n", $1, $2, $3, ($3 == 0 && $1 !~ /_error$/) ? "   <-- nobody is consuming" : "" }'

section "Parked in _error queues"
docker exec "$RABBIT" rabbitmqctl list_queues name messages --no-table-headers 2>/dev/null \
  | awk '$1 ~ /_error$/ && $2 > 0 { print "  " $1 ": " $2 }' | grep . || echo "  none"

section "Inventory for the products on this order"
for p in $(orderdb "select \"ProductId\" from \"OrderItem\" where \"OrderId\" = '$ID'"); do
  q="$(invdb "select \"QuantityAvailable\" from \"InventoryItems\" where \"ProductId\" = '$p'")"
  echo "  ${p:0:8}: ${q:-NO INVENTORY RECORD (reservation will be refused)}"
done

section "What the services logged about it (last lines mentioning $SHORT)"
for c in taskflow-order-api taskflow-inventory-api; do
  docker ps --format '{{.Names}}' | grep -qx "$c" || { echo "  $c: not running  <-- this alone stalls checkouts"; continue; }
  echo "  $c:"
  docker logs "$c" --since 24h 2>&1 | grep -i "$SHORT" | tail -4 | cut -c1-200 | sed 's/^/    /'
done

section "Circuit breaker / retry trouble in the last 10 minutes"
for c in taskflow-order-api taskflow-inventory-api; do
  n="$(docker logs "$c" --since 10m 2>&1 | grep -c -i 'circuit\|R-FAULT')"
  echo "  $c: $n line(s)"
done
