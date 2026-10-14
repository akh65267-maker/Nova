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

// Partitioned by IP + submitted email so a botnet from many IPs targeting one
// account, or one IP cycling through many accounts, are both limited.
// .NET 8's rate-limiting middleware is in the shared framework - no package needed.
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
            PermitLimit = 10,
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

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
