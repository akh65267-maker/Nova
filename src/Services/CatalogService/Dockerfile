FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
# Where product pictures live (a volume in docker-compose). Created here, owned by the app user, so
# the volume starts out writable by it.
RUN mkdir -p /data/product-images && chown $APP_UID /data/product-images
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Services/CatalogService/CatalogService.csproj", "Services/CatalogService/"]
RUN dotnet restore "./Services/CatalogService/CatalogService.csproj"
COPY . .
WORKDIR "/src/Services/CatalogService"
RUN dotnet build "./CatalogService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./CatalogService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CatalogService.dll"]
