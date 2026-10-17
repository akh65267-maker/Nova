using Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("gateway");

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3100"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});

// The caller's real address, from X-Forwarded-For, believed only from the networks in
// ForwardedHeaders:KnownNetworks (whatever fronts the gateway: the shop's server, an ingress). The
// gateway then passes that address on to the services, whose sign-in limits are per caller.
var trustForwardedFor = TrustedForwardedHeaders.Configure(builder.Services, builder.Configuration);

var app = builder.Build();

if (trustForwardedFor)
    app.UseForwardedHeaders();

app.UseSerilogRequestLogging();

app.UseCors();

app.MapReverseProxy();

app.Run();

// Lets the integration tests host the gateway (WebApplicationFactory needs a public Program).
public partial class Program
{
}
