# Architecture

.NET 8 microservices behind a YARP reverse proxy. Synchronous traffic is HTTP through the gateway; cross-service workflow is asynchronous over RabbitMQ (MassTransit). Each service owns its own storage. There is no shared database and no service-to-service HTTP call anywhere in the code.

```text
Browser / client
      │  HTTP
      ▼
ApiGateway (YARP, path-prefix routing, CORS)
      │
      ├── /users/**      → UserService      (Postgres: userflow)
      ├── /products/**   → CatalogService   (Postgres: catalogflow)
      ├── /inventory/**  → InventoryService (Postgres: inventoryflow)
      ├── /basket/**     → BasketService    (Redis)
      ├── /orders/**     → OrderService     (Postgres: orderflow + saga)
      └── /tasks/**      → TaskService      (out of scope)

RabbitMQ  ── OrderSubmitted / ReserveStock / StockReserved
             StockReservationFailed / ReleaseStock / UserRegistered
```

## Solution layout

```text
src/Gateway/ApiGateway          YARP reverse proxy
src/Services/<Name>Service      one project per service
src/Shared/Contracts            message contracts shared by publishers+consumers
src/Shared/Observability        AddObservability(): Serilog + OpenTelemetry wiring
tests/<Name>.UnitTests          xUnit unit tests per service
tests/Saga.IntegrationTests     real Postgres + RabbitMQ via Testcontainers
deploy/docker-compose.yml       full local topology
```

Each service is a single project using folder-level layering, not separate assemblies:

```text
Domain/            entities with behaviour + repository interfaces
Application/       service classes (or MediatR handlers), DTOs, consumers, saga
Infrastructure/    EF Core DbContext, repositories, security primitives
Program.cs         DI, auth, MassTransit, minimal-API endpoints (no controllers)
```

## Gateway

`src/Gateway/ApiGateway/Program.cs` does three things: YARP `LoadFromConfig`, a CORS default policy, and Serilog request logging.

- Routing is pure path-prefix catch-all per cluster; destinations come from configuration (`appsettings.json` for local run, `ReverseProxy__Clusters__*` env vars in compose).
- The gateway performs **no authentication, authorization, rate limiting or request transformation.** It forwards `Authorization` headers untouched; every service validates the JWT itself.
- CORS allowed origins come from `Cors:AllowedOrigins`, defaulting to `http://localhost:3100` — the only trace in the repo of an expected frontend.

## Ports

| Component | Local (`launchSettings`/appsettings) | Compose host port |
|---|---|---|
| Gateway | 8080 targets | 8000 |
| TaskService | 8080 | 8080 |
| UserService | 8081 | 8081 |
| CatalogService | 8082 | 8082 |
| InventoryService | 8083 | 8083 |
| BasketService | 8084 | 8084 |
| OrderService | 8085 | 8085 |
| RabbitMQ | — | 5672, 15672 (management) |
| Redis | — | 6379 |
| Jaeger | — | 16686 (UI), 4317/4318 (OTLP) |
| Prometheus | — | 9090 |
| Grafana | — | 3000 |

Postgres instances are separate containers per service (5432–5436 on the host). See [database.md](database.md).

## Observability

`Observability.AddObservability(serviceName, params additionalActivitySources)` is called first in every `Program.cs`:

- **Serilog** to console, enriched with `Service` and the current `TraceId`/`SpanId` (`Enrich.WithSpan`).
- **OpenTelemetry tracing** with ASP.NET Core + HttpClient instrumentation, plus `"MassTransit"` as an extra activity source for the services that use the bus (User, Inventory, Order, Task). Catalog, Basket and the gateway pass no extra source.
- OTLP exporter endpoint is **not** hardcoded — it is read from the standard `OTEL_EXPORTER_OTLP_*` env vars (compose points them at Jaeger).
- **OpenTelemetry metrics** with the ASP.NET Core + HttpClient meters (RED signals), the `MassTransit` meter, and any app meter named `TaskManager.*`. Exported by **scrape**, not OTLP: every service serves `GET /metrics` and Prometheus pulls it. See [observability.md](observability.md).
- The OpenTelemetry trace id doubles as the correlation id; MassTransit propagates it onto messages, so one id spans HTTP request → message → consumer. There is no hand-rolled correlation header.
- RabbitMQ's own broker/queue metrics come from the `rabbitmq_prometheus` plugin on port 15692.

Every service exposes `GET /health`: `AddDbContextCheck<T>` for the EF services, a Redis `PING` check in BasketService. The gateway has no health endpoint.

## Cross-cutting API conventions

- Minimal APIs only; no MVC controllers anywhere.
- `ApiExceptionHandler` (one copy per service, same shape) maps `ArgumentException` → `400` `ProblemDetails` and passes everything else through. It also maps `BadHttpRequestException` (what ASP.NET Core throws for a body that is not valid JSON for the endpoint, or a query value of the wrong type) to a client error: the framework's own 4xx (413, 408) or else `400`, with a **fixed** message from `Observability.BadRequestHandling`, because the framework's message names internal parameters and types and quotes what the caller sent. Before that, only OrderService did so; the other four answered garbage with a `500` and an error in the log. UserService additionally has `InvalidCredentialsException`; InventoryService has `InsufficientStockException` (surfaced through messaging, not HTTP).
- Swagger is enabled in Development only.
- `app.UseHttpsRedirection()` is last in each pipeline; containers run plain HTTP on 8080.
- OrderService is the only service that registers `JsonStringEnumConverter` (so `OrderStatus` serializes as a string).

## Build & CI

- `TaskManager.slnx` (slnx solution format).
- `.github/workflows/ci.yml` runs three parallel jobs on every push and pull request to `main`: `build-and-test` (Release build of the solution, then one `dotnet test` over every test project except `Saga.IntegrationTests`), `migration-drift` (`dotnet ef migrations has-pending-model-changes` for each service), and `integration-tests` (`Saga.IntegrationTests`, kept separate because it starts real Postgres and RabbitMQ containers via Testcontainers and is slower).
- `.github/workflows/deploy-azure.yml` is manual-only and disabled unless `AZURE_DEPLOY_ENABLED` is set; see [azure-deployment.md](azure-deployment.md).
