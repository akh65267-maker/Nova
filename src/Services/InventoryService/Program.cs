using System.Text;
using InventoryService;
using InventoryService.Application;
using InventoryService.Application.Consumers;
using InventoryService.Application.Inventory;
using InventoryService.Domain;
using InventoryService.Infrastructure.Messaging;
using InventoryService.Infrastructure.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("inventory-service", "MassTransit");

var connectionString = builder.Configuration.GetConnectionString("InventoryDatabase")
    ?? throw new InvalidOperationException("InventoryDatabase connection string is missing.");

// Deliberately not using EnableRetryOnFailure: this DbContext is used by
// MassTransit's EF Core inbox/outbox on the ReserveStock/ReleaseStock
// receive endpoints, which wrap each message in an explicit transaction -
// incompatible with EF's retrying execution strategy. Message-level
// retry (UseMessageRetry, below) covers this instead.
builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<InventoryDbContext>();

builder.Services.AddScoped<IInventoryRepository, InventoryRepository>();
builder.Services.AddScoped<InventoryItemsService>();

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key configuration is missing.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "TaskManager";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "TaskManager";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<InventoryDbContext>(InventoryOutbox.Configure);

    x.AddConsumer<ReserveStockConsumer>();
    x.AddConsumer<ReleaseStockConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        // Retries a handful of times with backoff for transient failures,
        // then a circuit breaker stops hammering a dependency that's
        // genuinely down. Applies to every endpoint configured below.
        cfg.UseMessageRetry(r => r.Intervals(100, 500, 1000, 5000));

        // Outside it, a much longer retry for the database being unreachable altogether (the
        // last retry registered is the outer one). A reservation parked in an _error queue is
        // not safe to replay once its order has been cancelled, so ride the outage out instead.
        // See DatabaseOutage.
        cfg.UseMessageRetry(DatabaseOutage.ConfigureRetry);
        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        // Explicit receive endpoints (not convention-based ConfigureEndpoints)
        // so the EF Core inbox can be attached: it deduplicates by
        // MessageId+ConsumerId, closing the gap where a redelivered
        // ReserveStock could double-reserve stock. Endpoint names kept
        // identical to what the convention already produced.
        cfg.ReceiveEndpoint("ReserveStock", e =>
        {
            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
            e.ConfigureConsumer<ReserveStockConsumer>(context);
        });

        cfg.ReceiveEndpoint("ReleaseStock", e =>
        {
            e.UseEntityFrameworkOutbox<InventoryDbContext>(context);
            e.ConfigureConsumer<ReleaseStockConsumer>(context);
        });
    });
});

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/inventory", async (
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    var items = await service.GetAllAsync(cancellationToken);

    return Results.Ok(items);
});

app.MapGet("/inventory/{productId:guid}", async (
    Guid productId,
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    var item = await service.GetByProductIdAsync(productId, cancellationToken);

    return item is null ? Results.NotFound() : Results.Ok(item);
});

app.MapPost("/inventory", async (
    CreateInventoryItemRequest request,
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    await service.CreateAsync(request, cancellationToken);

    return Results.Created($"/inventory/{request.ProductId}", new { productId = request.ProductId });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/inventory/{productId:guid}/restock", async (
    Guid productId,
    RestockRequest request,
    InventoryItemsService service,
    CancellationToken cancellationToken) =>
{
    var item = await service.RestockAsync(productId, request.Quantity, cancellationToken);

    return item is null ? Results.NotFound() : Results.Ok(item);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();

public partial class Program
{
}
