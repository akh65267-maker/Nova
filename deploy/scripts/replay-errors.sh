#!/usr/bin/env bash
# Moves messages from MassTransit *_error queues back to the queue they failed on.
#
# A message lands in <queue>_error when it exhausted its retries. Nothing consumes those
# queues, so after an outage (a database restart, a broker blip) the work they carry just
# sits there: orders that never got a saga, stock responses that were never counted. Once
# the cause is fixed, moving them back is what finishes that work.
#
#   deploy/scripts/replay-errors.sh --list
#   deploy/scripts/replay-errors.sh OrderSagaState_error [other_error ...]
#   deploy/scripts/replay-errors.sh --all
#   add --dry-run to any of them to see what would move
#
# It uses RabbitMQ's shovel plugin, which moves messages with their headers and body intact
# and acknowledges each one only after the destination has it, so nothing is lost if this is
# interrupted. The plugin is enabled in deploy/rabbitmq/enabled_plugins.
#
# READ BEFORE USING. This replays EVERYTHING in the queue, with no way to pick:
#   - Do it soon after the outage. A message about an order that has long since been
#     cancelled (a ReserveStock, say) takes stock for an order that no longer exists, and
#     whether that is released again depends on that order's saga still being around
#     (Checkout:TimedOutRetention, 24 h by default).
#   - A message that fails for a reason other than the outage (a bug, bad data) will fail
#     again and come back to the _error queue. The counts printed at the end show it.
#   - Run it with the services up and healthy, or the messages just fail again.

set -euo pipefail

CONTAINER="${RABBITMQ_CONTAINER:-taskflow-rabbitmq}"
WAIT_S="${REPLAY_WAIT_S:-180}"

die() { echo "error: $*" >&2; exit 1; }
rmq() { docker exec "$CONTAINER" "$@"; }

QUEUES=() ALL=false LIST=false DRY=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --all)     ALL=true; shift ;;
    --list)    LIST=true; shift ;;
    --dry-run) DRY=true; shift ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    -*)        die "unknown option $1" ;;
    *)         QUEUES+=("$1"); shift ;;
  esac
done

docker ps --format '{{.Names}}' | grep -qx "$CONTAINER" || die "container '$CONTAINER' is not running"

depth() { rmq rabbitmqctl list_queues name messages --no-table-headers 2>/dev/null | awk -v q="$1" '$1 == q { print $2 }'; }
error_queues() { rmq rabbitmqctl list_queues name messages --no-table-headers 2>/dev/null | awk '$1 ~ /_error$/ && $2 > 0 { print $1 }'; }

if $LIST; then
  echo "Messages waiting in _error queues:"
  found=false
  while read -r q; do
    [[ -n "$q" ]] || continue
    printf '  %-40s %s\n' "$q" "$(depth "$q")"; found=true
  done < <(error_queues)
  $found || echo "  none"
  exit 0
fi

if $ALL; then
  mapfile -t QUEUES < <(error_queues)
fi
[[ ${#QUEUES[@]} -gt 0 ]] || { echo "Nothing to replay."; exit 0; }

rmq rabbitmq-plugins list -e -m 2>/dev/null | grep -qx rabbitmq_shovel \
  || die "the rabbitmq_shovel plugin is not enabled. Add rabbitmq_shovel and rabbitmq_shovel_management to deploy/rabbitmq/enabled_plugins and recreate the broker, or run: docker exec $CONTAINER rabbitmq-plugins enable rabbitmq_shovel rabbitmq_shovel_management"

for error_queue in "${QUEUES[@]}"; do
  [[ "$error_queue" == *_error ]] || die "'$error_queue' is not an _error queue"
  target="${error_queue%_error}"

  before="$(depth "$error_queue")"
  [[ -n "$before" ]] || die "queue '$error_queue' does not exist"
  [[ -n "$(depth "$target")" ]] || die "'$error_queue' has no input queue '$target' to move messages back to"
  [[ "$before" -gt 0 ]] || { echo "$error_queue: empty, skipping"; continue; }

  if $DRY; then
    echo "would move $before message(s): $error_queue -> $target"
    continue
  fi

  name="replay-$error_queue"
  echo "moving $before message(s): $error_queue -> $target"
  # src-delete-after = queue-length: move what is there now, then stop. Messages that fail
  # again and arrive later are not chased, so this cannot loop.
  value="$(printf '{"src-protocol":"amqp091","src-uri":"amqp://","src-queue":"%s","dest-protocol":"amqp091","dest-uri":"amqp://","dest-queue":"%s","src-delete-after":"queue-length","ack-mode":"on-confirm"}' "$error_queue" "$target")"
  rmq rabbitmqctl set_parameter shovel "$name" "$value" >/dev/null

  # The shovel removes itself once it has moved what was there. Wait for that rather than
  # for the queue to be empty: a message that fails again lands straight back in it.
  waited=0
  while rmq rabbitmqctl list_parameters --no-table-headers 2>/dev/null | awk -v n="$name" '$2 == n { f = 1 } END { exit !f }'; do
    sleep 2; waited=$((waited + 2))
    if [[ "$waited" -ge "$WAIT_S" ]]; then
      rmq rabbitmqctl clear_parameter shovel "$name" >/dev/null 2>&1 || true
      die "the shovel for $error_queue did not finish within ${WAIT_S}s and was removed; $(depth "$error_queue") message(s) remain"
    fi
  done
  rmq rabbitmqctl clear_parameter shovel "$name" >/dev/null 2>&1 || true
done

if ! $DRY; then
  echo
  echo "Moved. Give the services a moment, then check what failed again:"
  sleep 10
  "$0" --list
fi
