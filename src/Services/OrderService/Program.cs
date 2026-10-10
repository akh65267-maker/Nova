using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Observability;
using OrderService;
using OrderService.Application;
using OrderService.Application.Catalog;
using OrderService.Application.Orders;
using OrderService.Application.Sagas;
using OrderService.Domain;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Catalog;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Observability;
using OrderService.Infrastructure.Persistence;
using Serilog;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("order-service", "MassTransit");

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var connectionString = builder.Configuration.GetConnectionString("OrderDatabase")
    ?? throw new InvalidOperationException("OrderDatabase connection string is missing.");

// Deliberately not using EnableRetryOnFailure here: EF Core's retrying
// execution strategy doesn't support user-initiated transactions, and
// MassTransit's saga repository wraps each message in exactly that kind
// of explicit transaction (visible as "SELECT ... FOR UPDATE" + saga
// save in one transaction in the logs). Message-level retry
// (UseMessageRetry, below) already covers this class of transient
// failure at a layer that's actually compatible with the saga.
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderDbContext>();

builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<OrdersService>();

// Where prices come from. Required: an order service that cannot price an order must not
// start quietly and then trust the caller instead.
var catalogBaseUrl = builder.Configuration["Catalog:BaseUrl"]
    ?? throw new InvalidOperationException("Catalog:BaseUrl is missing.");
builder.Services.AddHttpClient<IProductPriceProvider, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddSingleton<OrderMetrics>();
builder.Services.AddHostedService<OrderMetricsCollector>();

var checkoutTimeout = builder.Configuration
    .GetSection(CheckoutTimeoutOptions.SectionName)
    .Get<CheckoutTimeoutOptions>() ?? new CheckoutTimeoutOptions();

builder.Services.AddSingleton(checkoutTimeout);
builder.Services.AddHostedService<CheckoutTimeoutSweeper>();

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
    x.AddEntityFrameworkOutbox<OrderDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.AddSagaStateMachine<OrderSagaStateMachine, OrderSagaState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<OrderDbContext>();
            r.UsePostgres();
        });

    // The transactional inbox/outbox on the saga's receive endpoint, which
    // ConfigureEndpoints creates (there is nothing else for it to touch here).
    // Two problems it fixes, both reproduced by Saga.IntegrationTests:
    //
    //  - Outbox: the saga publishes ReserveStock/ReleaseStock while its own
    //    transaction is still open. Without this the command leaves immediately,
    //    so a fast InventoryService can answer before the saga row is committed;
    //    StockReserved then finds no saga and is dropped without a fault, and the
    //    checkout hangs. With it, publishes are held until the transaction commits.
    //  - Inbox: delivery is at-least-once, and a redelivered StockReserved used to
    //    increment ResponseCount again, confirming a multi-item order before every
    //    item had answered. The inbox deduplicates by MessageId and consumer.
    //
    // Applied through ConfigureEndpoints because the saga endpoint is not declared
    // explicitly. It sits alongside the bus-level UseMessageRetry below the same way
    // it does on InventoryService's explicit endpoints. The saga repository above
    // uses the same scoped DbContext, which is what lets both share one transaction.
    x.AddConfigureEndpointsCallback((context, name, endpoint) =>
        endpoint.UseEntityFrameworkOutbox<OrderDbContext>(context));

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitMqPort = builder.Configuration.GetValue<ushort?>("RabbitMq:Port") ?? 5672;

        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", rabbitMqPort, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });

        cfg.ReceiveEndpoint("debug-order-submitted", e =>
        {
            e.Handler<OrderSubmitted>(context =>
            {
                Console.WriteLine(
                    $"DEBUG OrderSubmitted received: {context.Message.OrderId}");

                return Task.CompletedTask;
            });
        });

        // StockReserved and StockReservationFailed for the same order can
        // arrive nearly simultaneously (concurrent ReserveStock calls), and
        // both try to lock/update the same saga row. Postgres correctly
        // rejects one under serializable isolation (40001: could not
        // serialize access due to concurrent update) - that's an expected,
        // recoverable race, not a real failure, so retry a few times rather
        // than faulting the message.
        //
        // Outside it, a much longer retry for the database being unreachable altogether
        // (registered second: the last retry registered is the outer one). Without it a
        // restart of a few seconds parked messages in _error queues that nothing reads.
        // See DatabaseOutage.
        cfg.UseMessageRetry(r => r.Intervals(100, 250, 500, 1000));
        cfg.UseMessageRetry(DatabaseOutage.ConfigureRetry);

        // Stops hammering a dependency that's genuinely down (as opposed to
        // the momentary concurrency conflicts UseMessageRetry above handles)
        // instead of retrying every message indefinitely.
        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        cfg.ConfigureEndpoints(context);
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

app.MapGet("/orders", async (
    ClaimsPrincipal user,
    OrdersService service,
    CancellationToken cancellationToken) =>
{
    var orders = await service.GetAllAsync(user.GetUserId(), cancellationToken);

    return Results.Ok(orders);
}).RequireAuthorization();

app.MapGet("/orders/{id:guid}", async (
    Guid id,
    ClaimsPrincipal user,
    OrdersService service,
    CancellationToken cancellationToken) =>
{
    var order = await service.GetByIdAsync(id, user.GetUserId(), cancellationToken);

    return order is null ? Results.NotFound() : Results.Ok(order);
}).RequireAuthorization();

app.MapPost("/orders", async (
    CreateOrderRequest request,
    ClaimsPrincipal user,
    OrdersService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.CreateAsync(request, user.GetUserId(), cancellationToken);

    return Results.Created($"/orders/{id}", new { id });
}).RequireAuthorization();

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();

public partial class Program
{
}
