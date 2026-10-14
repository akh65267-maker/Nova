#!/bin/sh
# Runs each migration bundle against its own database. Connection strings come
# from the job's environment (Key Vault-backed secrets); nothing is baked in.
# `set -e` stops at the first failure so a half-migrated set is never reported
# as success - the job then fails and the deploy script stops before starting apps.
set -eu

# A migration bundle boots the service's own host to obtain its configured
# DbContext, and every service's Program.cs throws at startup if Jwt:Key is
# missing. Without a value EF falls back to a bare DbContext and fails with
# "Unable to resolve service for type DbContextOptions". Nothing here ever signs
# or validates a token, so a placeholder is enough - and deliberately not the
# real key, which the migration job has no business holding.
export Jwt__Key="${Jwt__Key:-migration-job-placeholder-never-used-to-sign-tokens}"

# The same reasoning applies to OrderService's catalog address, which its Program.cs also
# requires at startup (orders are priced against the catalog). A migration never calls it.
export Catalog__BaseUrl="${Catalog__BaseUrl:-http://migration-job-placeholder-never-called}"

migrate() {
  service="$1"
  connection="$2"
  echo "==> Migrating ${service}"
  "/app/efbundle-${service}" --connection "${connection}"
}

migrate UserService      "${ConnectionStrings__UserDatabase}"
migrate CatalogService   "${ConnectionStrings__CatalogDatabase}"
migrate InventoryService "${ConnectionStrings__InventoryDatabase}"
migrate OrderService     "${ConnectionStrings__OrderDatabase}"

echo "==> All migrations applied"
