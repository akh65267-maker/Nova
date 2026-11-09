# Azure deployment

Everything needed to deploy TaskManager to Azure Container Apps **once a subscription exists**. Nothing has been deployed, no Azure account is connected to this repository, and local development is unchanged and never depends on Azure.

The template is Bicep (`infra/`), driven by one script (`infra/scripts/deploy-azure.sh`) and an opt-in, currently inert GitHub workflow. No application code was modified to make this possible.

## What was and was not verified

There is no subscription, so **no resource here has ever been created**. What was checked offline:

| Checked | How |
|---|---|
| Template compiles, every resource property is valid against Azure's published schemas | `bicep build` (0 errors, 0 warnings) |
| All 9 environment x stage parameter combinations compile; an invalid stage is rejected | `bicep build-params` |
| RabbitMQ, Prometheus and Grafana images build; the Prometheus config and all 3 alert rules are valid; the RabbitMQ Prometheus plugin is enabled | Docker build + `promtool` |
| The migration image builds four EF bundles and, run against an empty Postgres, creates every table in all four databases, including `OrderSagaStates.CreatedAtUtc` | Docker + throwaway Postgres |
| Deploy script argument handling, secret generation (idempotent, 600 on Linux), syntax | run offline |
| Deploy workflow | `actionlint` |

**Not verified — treat as assumptions to confirm on the first real deployment:**

- ACA in-environment name resolution: the gateway calls `http://users-api/` (internal ingress, port 80) and Prometheus scrapes `users-api:80`.
- RabbitMQ on an Azure Files (SMB) volume with the mount options in `main.bicep`.
- Private mode end to end: private endpoints, private DNS, the internal environment, and APIM v2 outbound VNet integration.
- Azure Managed Redis on the stable `2025-07-01` API: property values, and TLS on port 10000.
- The managed OpenTelemetry agent actually delivering traces from these apps to Application Insights.
- Npgsql `SSL Mode=Require;Trust Server Certificate=true` against the flexible server.

## Architecture and mapping

| Existing component | Azure service | Local equivalent | Configured by | Status |
|---|---|---|---|---|
| 6 service images | Container Registry (managed-identity pull, admin user off) | `docker compose build` | `modules/acr.bicep` | Required |
| Gateway + 5 services | Container Apps (one environment, one app each, single revision) | `docker compose` | `modules/containerapp.bicep` | Required |
| 4 Postgres databases | PostgreSQL Flexible Server, v17 | Postgres containers | `modules/postgres.bicep`, connection string in Key Vault | Required |
| Basket Redis | **Azure Managed Redis** (not Azure Cache for Redis, see below) | `redis:7` | `modules/redis.bicep`, connection string in Key Vault | Required |
| RabbitMQ + MassTransit | RabbitMQ **container** on ACA, TCP ingress, Azure Files volume | RabbitMQ container | `main.bicep` (`rabbitmq` app) | Required |
| `Jwt:Key`, DB and broker passwords | Key Vault, read via a user-assigned managed identity | `deploy/.env` | ACA secret references | Required |
| Serilog console output | Log Analytics (automatic) | `docker logs` | `modules/monitoring.bicep` | Required |
| OpenTelemetry traces | Application Insights via ACA's managed OTel agent | Jaeger | `modules/containerapps-env.bicep` | Recommended |
| Prometheus, Grafana, alerts | Prometheus + Grafana as container apps | same stack in compose | `infra/images/{prometheus,grafana}` | Recommended |
| Migrations (nothing runs them at startup) | Container Apps Job running EF bundles | manual `dotnet ef` | `infra/images/migrations`, `modules/migration-job.bicep` | Required |
| Edge, throttling | API Management in front of the YARP gateway | none | `modules/apim.bicep` | Recommended |
| Private networking | VNet, delegated subnets, private endpoints, private DNS | n/a | `modules/network.bicep`, `networkMode` | Optional |
| Operator access | Microsoft Entra ID (Azure RBAC) | n/a | outside the template | Optional |
| Blob Storage | **Not used.** No file or object storage exists in the application. A storage account exists only for RabbitMQ's Azure Files share. | n/a | `modules/storage.bicep` | Not required |

Azure Managed Redis replaces the service you asked for because Azure Cache for Redis Basic/Standard/Premium is retiring (2028-09-30) and already blocked for new customers; Managed Redis is its successor. BasketService needs only GET/SET, so nothing depends on the tier.

### Request path

```text
browser -> [APIM] -> gateway (YARP, unchanged) -> users-api / catalog-api / inventory-api /
                                                   basket-api / order-api   (internal ingress)
services <-> rabbitmq (TCP 5672, internal)       services -> Postgres / Redis (secrets from Key Vault)
```

APIM sits in front of the existing gateway rather than replacing it, so the routing table stays in one place (`ApiGateway/appsettings.json`) and is not duplicated in Bicep. CORS is left to the gateway; APIM deliberately adds none, because two CORS layers emit duplicate `Access-Control-Allow-Origin` headers, which browsers reject.

## Local vs Azure configuration

The application reads only environment variables, so the two modes differ in who sets them.

| Setting | Local (`docker compose`) | Azure |
|---|---|---|
| Postgres | `ConnectionStrings__*` built in `docker-compose.yml`, password from `deploy/.env` | same variable names, value is a Key Vault secret, `SSL Mode=Require` |
| Redis | `ConnectionStrings__Redis=redis:6379` | same name, Managed Redis endpoint + key from Key Vault |
| RabbitMQ | `RabbitMq__Host=rabbitmq` + credentials from `deploy/.env` | `RabbitMq__Host=rabbitmq` (the ACA app name) + password from Key Vault |
| JWT | `Jwt__*` from `deploy/.env` | `Jwt__Key` from Key Vault; issuer/audience from parameters |
| Environment | `ASPNETCORE_ENVIRONMENT=Development` | `Production` (only Swagger branches on it) |
| Traces | `OTEL_EXPORTER_OTLP_*` -> Jaeger | injected by the ACA OTel agent -> Application Insights |
| Metrics | Prometheus scrape of `:8080/metrics` | Prometheus scrape of `<app>:80/metrics` |
| Migrations | manual | Container Apps Job, run by the deploy script before apps exist |
| CORS origins | default `http://localhost:3100` | `Cors__AllowedOrigins__N` from `corsAllowedOrigins` |

Local files (`deploy/.env`, `deploy/docker-compose.yml`) and Azure files (`infra/`, `infra/.secrets/`) are separate and share nothing. Azure secrets are never in the repository: parameter files call `readEnvironmentVariable()`, and the values come from your environment, `infra/.secrets/<env>.env` (git-ignored), or GitHub Environment secrets. The variable names are listed in `infra/env.azure.example`.

## Environments

Differences live only in `infra/params/<env>.bicepparam`.

| | dev | staging | prod |
|---|---|---|---|
| Network | public endpoints behind auth | private VNet + private endpoints | private VNet + private endpoints |
| Postgres | shared server, Burstable, no HA | shared server, Burstable | **server per service**, General Purpose, zone-redundant, geo backup |
| Replicas | 0-2 (order-api pinned to 1) | 1-3 | 2-10 |
| APIM | off | StandardV2 | StandardV2 |
| Key Vault purge protection | off | on | on |
| Grafana | public (password login) | internal | internal |

Every value is a parameter: environment name, region (the resource group's), name prefix, SKUs, replica and CPU/memory sizing (`scale`, with per-app overrides), Postgres topology and sizing, network mode, address space, image tag and stage. Resource names derive from `namePrefix`, the environment and a hash of the resource group id, so environments never collide. Container app names are fixed (`users-api`, `gateway`, ...) because they are also the in-environment DNS names the gateway and Prometheus use.

## Deploying

Prerequisites: the Azure CLI, `az login`, a subscription, `openssl`. The staging and prod parameter files contain placeholder frontend origins (`example.com`) to replace first.

```bash
# first run for an environment: create secrets locally, then deploy everything
infra/scripts/deploy-azure.sh dev all --location westeurope --generate-secrets

# preview the foundation without applying anything
infra/scripts/deploy-azure.sh dev foundation --what-if --location westeurope

# a later release: rebuild images at the current commit, migrate, roll out
infra/scripts/deploy-azure.sh dev all
```

`all` runs four stages, and each is safe to re-run:

1. **foundation** - registry, Key Vault, databases, Redis, Container Apps environment.
2. **images** - `az acr build` for every image, tagged with the git SHA.
3. **migrate** - adds the migration job, runs it and waits. Apps do not exist yet, because nothing applies migrations at startup and UserService seeds its admin user on boot, so it would crash against an empty database.
4. **apps** - every application, Prometheus/Grafana, and APIM if enabled.

Do not move an existing environment back to an earlier stage; Bicep's incremental mode will not delete the apps, but the template and reality will disagree.

**Rotating a secret** by re-running with a different value replaces it in Key Vault and rotates the Postgres admin password, so every service picks it up on its next revision. Do it deliberately.

### Enabling the pipeline

`.github/workflows/deploy-azure.yml` does nothing as committed: it is manual-only and skipped unless the repository variable `AZURE_DEPLOY_ENABLED` is `true`. To enable it, create an Entra app registration with a federated credential for this repository and a GitHub Environment (dev/staging/prod), give the app Contributor and User Access Administrator on the resource group, then set the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_LOCATION`, `TM_ADMIN_EMAIL` and the five `TM_*` secrets on the environment. Authentication is OIDC: no client secret is stored. Require a reviewer on the prod environment. `ci.yml` is untouched and remains the gate.

## Observability in Azure

- **Logs**: container stdout goes to Log Analytics automatically. Logs are *not* also sent through the OTel agent, since the apps log to the console and not OTLP; doing so would only duplicate them.
- **Traces**: the environment's managed OpenTelemetry agent forwards to Application Insights. It injects `OTEL_EXPORTER_OTLP_ENDPOINT` and `grpc` into each container, which is exactly what the shared `AddObservability` reads, so no code changes. Trace ids still appear in every log line and in the `X-Trace-Id` response header.
- **Metrics**: Application Insights cannot receive metrics through that agent, so the existing Prometheus + Grafana stack is self-hosted, reusing the committed dashboards (`deploy/grafana/dashboards`) and alert rules (`deploy/prometheus/rules`) unchanged. As in compose there is no Alertmanager: alerts are visible in Prometheus and Grafana, and notify no one.
- **Health**: readiness probes use `GET /health` (which includes the database or Redis check). Liveness is a plain TCP check on purpose, because `/health` depends on the database and would restart every replica during a brief database outage. The gateway has no `/health`, so it gets TCP probes throughout.

## Known limitations

- **RabbitMQ is a single node on Azure Files.** No clustering, and durability is only as good as an SMB share, so this is acceptable for dev/staging but is the weakest part of a production deployment. See *Application changes needing approval*.
- **TaskService is excluded.** It is not in the template, so `/tasks` returns 502 from the gateway and `UserRegistered` events have no consumer in Azure.
- **Gateway metrics are absent.** The gateway is not scraped, since its ingress is external and scraping it means TLS.
- **Prometheus scraping is a sample, not a sum,** once a service has more than one replica: the ingress load balancer sends each scrape to one of them. Prometheus itself is one replica with an ephemeral store (its TSDB is unsupported on SMB/NFS), so history is lost on restart.
- **`/metrics` is unauthenticated on every service.** In public mode the gateway's own `/metrics` is therefore reachable from the internet, and ACA cannot filter by path. Staging and prod avoid this (APIM blocks `/metrics`, and the gateway is internal). Use `gatewayAllowedCidrs` to make APIM the only way in for a public environment.
- **All services connect to Postgres as the server admin.** Bicep cannot create database roles, so per-service roles need a follow-up SQL step. `postgresTopology = perService` gives physical isolation instead.
- **Public mode's Postgres firewall admits every Azure service in every tenant** (the `0.0.0.0` rule). Use private mode for anything real.
- **ACR stays publicly reachable** in every mode (Entra-authenticated, pulled by managed identity), so CI can push. Lock it down once you have a build agent inside the network.
- **`order-api` needs at least one replica.** It hosts the checkout timeout sweeper and the outbox delivery poller; scaled to zero, stranded checkouts are never cancelled. This is pinned in `dev.bicepparam`.
- **The migration job is a Container Apps Job holding all four connection strings** (as Key Vault-backed secrets). It is manual-trigger, and runs only when the script starts it.
- **No queue-length autoscaling.** Consumers scale on HTTP concurrency only; scaling on RabbitMQ queue depth needs a KEDA rule.
- **The frontend is not deployed.** `taskmanager-web` is a separate repository with no Dockerfile, and its API URL is baked in at build time; hosting it (Static Web Apps or a container) is a later, separate decision. Point `corsAllowedOrigins` at wherever it lands.

## Application changes needing approval

Nothing below has been changed. Each would touch existing application code, so each is a decision for you.

| Change | Why | Files | Alternative used now |
|---|---|---|---|
| Switch MassTransit to **Azure Service Bus** | Managed, durable, production-grade broker; private endpoints need the Premium tier | `Program.cs` of User, Inventory, Order (and TaskService's consumer) | RabbitMQ container |
| **Passwordless** Postgres and Redis (managed identity) | No database password at all | `Program.cs` of User, Catalog, Inventory, Order, Basket (Npgsql/StackExchange token providers) | Passwords in Key Vault |
| **Azure Monitor metrics exporter** | Metrics in Application Insights instead of self-hosted Prometheus | `src/Shared/Observability/ObservabilityExtensions.cs` (one package, env-gated) | Self-hosted Prometheus/Grafana |
| `/health/live` that ignores dependencies; a gateway `/health` | Proper liveness/readiness split | each `Program.cs` | TCP liveness probe |
| Optional TLS and port for RabbitMQ | Lets a hosted broker work (User and Inventory hardcode port 5672, no TLS) | `Program.cs` of User, Inventory | RabbitMQ inside the environment |
| Fail startup on the placeholder `Jwt:Key` outside Development (already in `TODO.md`) | Guards a mis-configured deploy | each `Program.cs` | The key always comes from Key Vault here |
| Upgrade to .NET 10 LTS | .NET 8 support ends in November 2026 (please verify the date) | `*.csproj`, Dockerfiles | none - plan it before production |

Entra ID for **customers** (replacing UserService's self-issued JWT) is a much larger change than any above and is out of scope. Entra governs *operator* access only: Azure RBAC on the resources and Key Vault.

## Production readiness

The template makes prod deployable. Before relying on it:

- [ ] Decide the message broker (see above); single-node RabbitMQ on SMB is not enough for production.
- [ ] Put a real frontend origin into `prod.bicepparam`, and host the frontend.
- [ ] Add Alertmanager (or Azure Monitor alerts) so the checkout alerts notify someone.
- [ ] Per-service Postgres roles instead of the admin login.
- [ ] Size `postgresMaxPoolSize` (default 50, not measured on Azure) against the server SKU: pool x replicas, summed over the services sharing a server, must stay below its `max_connections` with headroom.
- [ ] Tighten `postgresConnectionOptions` to `SSL Mode=VerifyFull` once the CA chain is confirmed.
- [ ] Confirm every item under *Not verified* on staging first.
- [ ] Review SKUs and cost in the Azure pricing calculator; this repository states no prices.
- [ ] Keep the .NET 10 SDK and runtime current with servicing updates.

## Files

```text
infra/main.bicep                 orchestration; stages, naming, wiring
infra/modules/*.bicep            one module per Azure service + generic container app
infra/params/{dev,staging,prod}.bicepparam
infra/images/                    RabbitMQ, Prometheus, Grafana, migrations images
infra/scripts/deploy-azure.sh    the only entry point
infra/env.azure.example          variable names (no values)
.github/workflows/deploy-azure.yml   inert until enabled
.dockerignore                    root-context builds only (infra images)
```
