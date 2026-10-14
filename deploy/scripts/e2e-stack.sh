#!/usr/bin/env bash
# A throwaway copy of the whole stack for end-to-end tests (the frontend repository's CI runs
# its Playwright tests against it): fresh databases, throwaway secrets, one admin account.
#
#   deploy/scripts/e2e-stack.sh up      build and start it; waits until the gateway answers
#   deploy/scripts/e2e-stack.sh down    stop it and delete its data
#
# It writes deploy/.env with RANDOM secrets, so it refuses to run where a deploy/.env already
# exists (your real one). Set E2E_STACK_FORCE=1 to overwrite it anyway. The admin account's
# address and password are in that file afterwards (ADMIN_EMAIL, ADMIN_PASSWORD); this script
# never prints them. Set COMPOSE_PROJECT_NAME to keep it apart from another copy of the stack.
set -euo pipefail

DEPLOY="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="$(cd "$DEPLOY/.." && pwd)"
cd "$DEPLOY"

compose() { docker compose "$@"; }

random() { openssl rand -base64 "$1" | tr -d '/+=\n'; }

write_env() {
  if [[ -e .env && "${E2E_STACK_FORCE:-}" != 1 ]]; then
    echo "e2e-stack: deploy/.env already exists; refusing to overwrite it (E2E_STACK_FORCE=1 to do so)" >&2
    exit 1
  fi
  umask 077
  cat > .env <<EOF
POSTGRES_PASSWORD=$(random 24)
RABBITMQ_USER=e2e
RABBITMQ_PASSWORD=$(random 24)
JWT_KEY=$(random 48)
JWT_ISSUER=TaskManager
JWT_AUDIENCE=TaskManager
ADMIN_EMAIL=admin-e2e@example.com
ADMIN_PASSWORD=Aa1-$(random 18)
ADMIN_DISPLAY_NAME=E2E-Admin
GRAFANA_ADMIN_PASSWORD=$(random 18)
PGADMIN_EMAIL=pgadmin-e2e@example.com
PGADMIN_PASSWORD=$(random 18)
EOF
}

# The migration image creates every table; no service does it at startup, and UserService seeds
# the admin at startup, so the databases must be migrated BEFORE the applications start.
migrate() {
  local network password
  network="$(compose ps -q postgres-order | xargs docker inspect -f '{{range $name, $n := .NetworkSettings.Networks}}{{$name}}{{end}}')"
  password="$(grep -E '^POSTGRES_PASSWORD=' .env | cut -d= -f2-)"

  docker build -q -f "$ROOT/infra/images/migrations/Dockerfile" -t taskflow-e2e-migrations "$ROOT" >/dev/null
  docker run --rm --network "$network" \
    -e "ConnectionStrings__UserDatabase=Host=postgres-users;Port=5432;Database=userflow;Username=postgres;Password=$password" \
    -e "ConnectionStrings__CatalogDatabase=Host=postgres-catalog;Port=5432;Database=catalogflow;Username=postgres;Password=$password" \
    -e "ConnectionStrings__InventoryDatabase=Host=postgres-inventory;Port=5432;Database=inventoryflow;Username=postgres;Password=$password" \
    -e "ConnectionStrings__OrderDatabase=Host=postgres-order;Port=5432;Database=orderflow;Username=postgres;Password=$password" \
    taskflow-e2e-migrations
}

wait_for() {
  local url="$1" seconds="$2" waited=0
  until curl -sf --max-time 3 "$url" >/dev/null 2>&1; do
    sleep 3
    waited=$((waited + 3))
    if [[ "$waited" -ge "$seconds" ]]; then
      echo "e2e-stack: $url did not answer within ${seconds}s" >&2
      compose ps >&2 || true
      return 1
    fi
  done
}

# One product with stock, so the shop's checkout tests have something to buy. Made through the
# public API as the admin, like anyone else would; the secrets stay in the environment.
seed() {
  local email password token product_id
  email="$(grep -E '^ADMIN_EMAIL=' .env | cut -d= -f2-)"
  password="$(grep -E '^ADMIN_PASSWORD=' .env | cut -d= -f2-)"

  # users-api can still be starting when the catalog already answers, so ask for a few seconds.
  token=""
  for _ in $(seq 1 20); do
    token="$(curl -sf -X POST http://localhost:8000/users/login -H 'Content-Type: application/json' \
      -d "{\"email\":\"$email\",\"password\":\"$password\"}" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p' || true)"
    [[ -n "$token" ]] && break
    sleep 3
  done
  [[ -n "$token" ]] || { echo "e2e-stack: could not sign in as the admin to seed a product" >&2; return 1; }

  product_id="$(curl -sf -X POST http://localhost:8000/products -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d '{"name":"E2E Seeded Product","description":"Created by e2e-stack.sh so the checkout tests have something to buy.","price":19.99,"category":"Electronics"}' \
    | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
  [[ -n "$product_id" ]] || { echo "e2e-stack: could not create the seed product" >&2; return 1; }

  curl -sf -X POST http://localhost:8000/inventory -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
    -d "{\"productId\":\"$product_id\",\"quantityAvailable\":100000}" >/dev/null \
    || { echo "e2e-stack: could not create the seed product's stock" >&2; return 1; }
}

up() {
  write_env

  # Databases and the broker first, so they can be migrated before any application starts.
  compose up -d --wait postgres postgres-users postgres-catalog postgres-inventory postgres-order rabbitmq redis
  migrate

  compose up -d --build tasks-api users-api catalog-api inventory-api basket-api order-api gateway

  # The catalog answering through the gateway means the gateway and catalog-api are up;
  # seed then waits for users-api, which it needs to sign in.
  wait_for "http://localhost:8000/products" 240
  seed
  echo "e2e-stack: up"
}

down() {
  compose down -v --remove-orphans
  rm -f .env
}

case "${1:-}" in
  up) up ;;
  down) down ;;
  *) echo "usage: e2e-stack.sh up|down" >&2; exit 2 ;;
esac
