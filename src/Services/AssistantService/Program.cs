using System.Text;
using AssistantService.Application;
using AssistantService.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("assistant-service");

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
builder.Services.AddProblemDetails();

Uri Address(string key) => new(builder.Configuration[key] ?? throw new InvalidOperationException($"{key} is missing."));

builder.Services.AddHttpClient(SearchProductsTool.HttpClientName, c => { c.BaseAddress = Address("Assistant:CatalogUrl"); c.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddHttpClient(SearchProductsTool.InventoryClientName, c => { c.BaseAddress = Address("Assistant:InventoryUrl"); c.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddHttpClient(GetMyOrdersTool.HttpClientName, c => { c.BaseAddress = Address("Assistant:OrderUrl"); c.Timeout = TimeSpan.FromSeconds(10); });
// The model behind the assistant: a local Ollama by default, or the Anthropic API (Assistant:Provider = "anthropic").
// A small local model on a CPU can take a while to answer.
if (string.Equals(builder.Configuration["Assistant:Provider"], "anthropic", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Assistant:Anthropic:ApiKey"]))
        throw new InvalidOperationException("Assistant:Anthropic:ApiKey is missing.");
    builder.Services.AddHttpClient<IChatModel, AnthropicChatModel>(c => { c.BaseAddress = new Uri("https://api.anthropic.com/"); c.Timeout = TimeSpan.FromSeconds(120); });
}
else
{
    builder.Services.AddHttpClient<IChatModel, OllamaChatModel>(c => { c.BaseAddress = Address("Assistant:OllamaUrl"); c.Timeout = TimeSpan.FromSeconds(120); });
}
builder.Services.AddSingleton<IAssistantTool, SearchProductsTool>();
builder.Services.AddSingleton<IAssistantTool, GetMyOrdersTool>();
builder.Services.AddScoped<AssistantRunner>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

// Off unless switched on (Assistant:Enabled), so the service can ship before a model is ready.
app.MapPost("/assistant/chat", async (
    ChatRequest request,
    HttpRequest http,
    AssistantRunner runner,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    if (!configuration.GetValue<bool>("Assistant:Enabled"))
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The assistant is not switched on.");

    var message = request.Message?.Trim();
    if (string.IsNullOrEmpty(message) || message.Length > AssistantRunner.MaxMessageLength)
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Write a message of 1 to {AssistantRunner.MaxMessageLength} characters.");

    try
    {
        return Results.Ok(await runner.AskAsync(message, http.Headers.Authorization.ToString(), request.Cart, cancellationToken));
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The assistant's model is not available right now.");
    }
}).RequireAuthorization();

app.MapGet("/health", () => Results.Ok());

app.Run();

public sealed record ChatRequest(string? Message, List<CartLine>? Cart);

public partial class Program;
