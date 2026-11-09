# Applies every service's EF Core migrations, for the ACA migration job.
#
# No service runs migrations at startup, so a fresh Azure database is empty.
# This builds one self-contained migration bundle per DbContext and runs them in
# order against connection strings supplied by the job.
# Build context: repository root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# A tool of this image's own, deliberately NOT the repo's .config/dotnet-tools.json:
# that manifest pins dotnet-ef 10.x with no roll-forward, which cannot run on an
# 8.0 SDK. The 10.0 tool matches the EF Core 10.0.x the services reference.
RUN dotnet tool install --tool-path /tools dotnet-ef --version "10.0.*"
ENV PATH="/tools:${PATH}"

COPY src src

# The output must NOT be named after the service. The tool generates a temporary
# project named after --output and references the real one; with matching names
# NuGet fails with "Ambiguous project name 'UserService'".
#
# Bundling builds each service and loads its DbContext through the app host, the
# same way CI's `has-pending-model-changes` does, using the placeholder values
# committed in appsettings.json. No database is contacted here.
RUN set -eux; \
    for service in UserService CatalogService InventoryService OrderService; do \
      dotnet ef migrations bundle \
        --project "src/Services/${service}/${service}.csproj" \
        --self-contained -r linux-x64 --force \
        --output "/out/efbundle-${service}"; \
    done

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /out/ ./
COPY --chmod=0755 infra/images/migrations/entrypoint.sh ./entrypoint.sh
USER $APP_UID
ENTRYPOINT ["/bin/sh", "/app/entrypoint.sh"]
