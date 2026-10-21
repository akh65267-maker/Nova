using System.Security.Claims;
using System.Text;
using BasketService;
using BasketService.Application;
using BasketService.Application.Baskets;
using BasketService.Domain;
using BasketService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("basket-service");

var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis connection string is missing.");

// AbortOnConnectFail = false: retry in the background instead of crashing the
// service on startup if Redis isn't reachable yet (a real race against
// docker-compose's depends_on/healthcheck, which only gates the *next*
// container's start, not the exact moment this process calls Connect).
var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AbortOnConnectFail = false;

var redis = ConnectionMultiplexer.Connect(redisOptions);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

builder.Services.AddHealthChecks()
    .AddCheck("redis", () =>
    {
        try
        {
            redis.GetDatabase().Ping();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(exception: ex);
        }
    });

builder.Services.AddScoped<IBasketRepository, RedisBasketRepository>();
builder.Services.AddScoped<BasketsService>();

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

app.MapGet("/basket", async (
    ClaimsPrincipal user,
    BasketsService service,
    CancellationToken cancellationToken) =>
{
    var basket = await service.GetAsync(user.GetUserId(), cancellationToken);

    return Results.Ok(basket);
}).RequireAuthorization();

app.MapPut("/basket", async (
    ReplaceBasketRequest request,
    ClaimsPrincipal user,
    BasketsService service,
    CancellationToken cancellationToken) =>
{
    var basket = await service.ReplaceAsync(user.GetUserId(), request, cancellationToken);

    return Results.Ok(basket);
}).RequireAuthorization();

app.MapPost("/basket/items", async (
    AddBasketItemRequest request,
    ClaimsPrincipal user,
    BasketsService service,
    CancellationToken cancellationToken) =>
{
    var basket = await service.AddItemAsync(user.GetUserId(), request, cancellationToken);

    return Results.Ok(basket);
}).RequireAuthorization();

app.MapDelete("/basket/items/{productId:guid}", async (
    Guid productId,
    ClaimsPrincipal user,
    BasketsService service,
    CancellationToken cancellationToken) =>
{
    var basket = await service.RemoveItemAsync(user.GetUserId(), productId, cancellationToken);

    return Results.Ok(basket);
}).RequireAuthorization();

app.MapDelete("/basket", async (
    ClaimsPrincipal user,
    BasketsService service,
    CancellationToken cancellationToken) =>
{
    await service.ClearAsync(user.GetUserId(), cancellationToken);

    return Results.NoContent();
}).RequireAuthorization();

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
