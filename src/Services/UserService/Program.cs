using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;
using UserService;
using UserService.Application;
using UserService.Application.Users;
using UserService.Domain;
using UserService.Infrastructure;
using UserService.Infrastructure.Persistence;
using UserService.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("user-service", "MassTransit");

var connectionString = builder.Configuration.GetConnectionString("UserDatabase")
    ?? throw new InvalidOperationException("UserDatabase connection string is missing.");

// Deliberately not using EnableRetryOnFailure: this DbContext backs
// MassTransit's transactional outbox (UseBusOutbox), which wraps
// SaveChanges in a way that's incompatible with EF's retrying
// execution strategy.
builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<UserDbContext>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddScoped<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<UsersService>();

// Refresh tokens: how long a sign-in lasts ("RefreshTokens" in configuration), the store, the service
// that rotates them, and the sweeper that deletes expired ones.
builder.Services.AddSingleton(builder.Configuration.GetSection("RefreshTokens").Get<RefreshTokenOptions>()?.Validated() ?? RefreshTokenOptions.Default);
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<ISessionService, SessionService>();
builder.Services.AddHostedService<RefreshTokenCleanupService>();
builder.Services.AddSingleton(TimeProvider.System);

// The caller's real address, from the gateway's X-Forwarded-For, believed only from the networks
// in ForwardedHeaders:KnownNetworks. Without it every caller is "the gateway" to the login rate
// limit below, and a few failed logins a minute from anyone would block everybody.
var trustForwardedFor = TrustedForwardedHeaders.Configure(builder.Services, builder.Configuration);

// How many wrong passwords lock an account, and for how long ("Login:Lockout" in configuration).
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Login:Lockout").Get<LoginLockoutPolicy>()?.Validated() ?? LoginLockoutPolicy.Default);

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
    x.AddEntityFrameworkOutbox<UserDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"] ?? "localhost", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMq:Password"] ?? "guest");
        });
    });
});

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

// .NET 8's rate-limiting middleware is in the shared framework - no package needed.
// Partitioned by client address only: the email is in the request body, which has not been read
// at this point in the pipeline. The per-account limit is the lockout in UsersService.LoginAsync.
// Per client address. This is a guard against one address burning CPU on password hashing (each
// attempt costs about 100 ms) and against spraying many accounts from one address; guessing at a
// single account is stopped by the account lockout, whichever addresses the guesses come from.
var loginPermitPerMinute = builder.Configuration.GetValue("Login:IpPermitPerMinute", 60);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("login", httpContext =>
    {
        // The email is in the request body which hasn't been read yet at
        // middleware time, so we partition on IP only at this layer. The
        // combined key (IP + email) would require reading the body here,
        // which interferes with model binding downstream. IP-only still
        // stops per-IP spray attacks and the CPU-amplification DoS.
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = loginPermitPerMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,   // one bucket per 10 seconds
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0           // reject immediately; no queuing
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (trustForwardedFor)
    app.UseForwardedHeaders();

app.UseSerilogRequestLogging();

// Seeds one admin account from configuration if it doesn't exist yet.
// There's no invite/promote-user flow, so this is the only way an Admin
// account comes to exist. The default password below is a dev-only
// placeholder - production deployments must override Admin:Password via
// a real secret, not commit a real password to appsettings.json.
using (var scope = app.Services.CreateScope())
{
    var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

    var adminEmail = app.Configuration["Admin:Email"];
    var adminPassword = app.Configuration["Admin:Password"];
    var adminDisplayName = app.Configuration["Admin:DisplayName"] ?? "Admin";

    if (!string.IsNullOrWhiteSpace(adminEmail)
        && !string.IsNullOrWhiteSpace(adminPassword)
        && !await userRepo.EmailExistsAsync(adminEmail))
    {
        var admin = new User(adminEmail, adminDisplayName, passwordHasher.Hash(adminPassword), UserRole.Admin);
        await userRepo.AddAsync(admin);
        await userRepo.SaveChangesAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/users", async (
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var users = await service.GetAllAsync(cancellationToken);

    return Results.Ok(users);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));   // every user's email: admins only

app.MapGet("/users/{id:guid}", async (
    Guid id,
    ClaimsPrincipal caller,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    // Your own profile, or any if you are an admin. Anyone else gets the same 404 as for an id
    // that does not exist, so the endpoint cannot be used to find out which ids do.
    if (!UserAccess.CanView(caller, id))
        return Results.NotFound();

    var user = await service.GetByIdAsync(id, cancellationToken);

    return user is null ? Results.NotFound() : Results.Ok(user);
}).RequireAuthorization();

app.MapPost("/users", async (
    RegisterUserRequest request,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var id = await service.RegisterAsync(request, cancellationToken);

    return Results.Created($"/users/{id}", new { id });
});

app.MapPost("/users/login", async (
    LoginRequest request,
    UsersService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.LoginAsync(request, cancellationToken);

    return Results.Ok(result);
}).RequireRateLimiting("login");

// Exchange a refresh token for a new pair; the one presented is spent. The same per-address limit
// as login, since a stolen token can be tried here.
app.MapPost("/users/refresh", async (
    RefreshRequest request,
    ISessionService sessions,
    CancellationToken cancellationToken) =>
{
    var tokens = await sessions.RefreshAsync(request.RefreshToken, cancellationToken);

    return Results.Ok(new LoginResult(tokens.AccessToken, tokens.AccessExpiresAtUtc, tokens.RefreshToken, tokens.RefreshExpiresAtUtc));
}).RequireRateLimiting("login");

// End the sign-in a refresh token belongs to. Quiet if it is unknown or already ended. The access
// token already issued stays valid until it expires (see docs/authentication.md).
app.MapPost("/users/logout", async (
    LogoutRequest request,
    ISessionService sessions,
    CancellationToken cancellationToken) =>
{
    await sessions.EndAsync(request.RefreshToken, cancellationToken);

    return Results.NoContent();
}).RequireRateLimiting("login");

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();

// Lets the integration tests host this service (WebApplicationFactory needs a public Program).
public partial class Program
{
}
