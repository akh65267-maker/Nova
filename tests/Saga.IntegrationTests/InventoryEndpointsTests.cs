extern alias InventoryAlias;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

using InventoryDbContext = InventoryAlias::InventoryService.Infrastructure.Persistence.InventoryDbContext;
using InventoryItem = InventoryAlias::InventoryService.Domain.InventoryItem;

namespace Saga.IntegrationTests;

/// <summary>
/// Who may read what from InventoryService, over real HTTP with real tokens. Anyone may learn a
/// product's availability; the exact quantities are for administrators.
/// </summary>
public class InventoryEndpointsTests : IAsyncLifetime
{
    // The placeholder key every service carries in appsettings.Development.json.
    private const string JwtKey = "dev-only-insecure-signing-key-change-me-32bytes";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private WebApplicationFactory<InventoryAlias::Program> _factory = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(_db.GetConnectionString()).Options;
        await using (var context = new InventoryDbContext(options))
            await context.Database.MigrateAsync();

        _factory = new WebApplicationFactory<InventoryAlias::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:InventoryDatabase", _db.GetConnectionString());
            // An address that cannot resolve, so this host can never reach a real broker: these endpoints never touch it, and the bus simply
            // keeps trying to connect in the background.
            builder.UseSetting("RabbitMq:Host", "rabbitmq.invalid");
        });
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<Guid> SeedAsync(int quantity)
    {
        var item = new InventoryItem(Guid.NewGuid(), quantity);
        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(_db.GetConnectionString()).Options;
        await using var context = new InventoryDbContext(options);
        context.Add(item);
        await context.SaveChangesAsync();
        return item.ProductId;
    }

    private void SignInAs(string? role)
    {
        if (role == "anonymous")
        {
            _client.DefaultRequestHeaders.Authorization = null;
            return;
        }

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()) };
        if (role is not null)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer: "TaskManager",
            audience: "TaskManager",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)), SecurityAlgorithms.HmacSha256));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    [Fact]
    public async Task Availability_IsPublic_AndShowsAStatus_WithTheNumberOnlyWhenItIsSmall()
    {
        var few = await SeedAsync(3);
        var plenty = await SeedAsync(500);
        var none = await SeedAsync(0);
        SignInAs("anonymous");

        var fewBody = JsonDocument.Parse(await (await _client.GetAsync($"/inventory/{few}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("LowStock", fewBody.GetProperty("availability").GetString());
        Assert.Equal(3, fewBody.GetProperty("remaining").GetInt32());

        var plentyResponse = await _client.GetAsync($"/inventory/{plenty}");
        Assert.Equal(HttpStatusCode.OK, plentyResponse.StatusCode);
        var plentyText = await plentyResponse.Content.ReadAsStringAsync();
        var plentyBody = JsonDocument.Parse(plentyText).RootElement;
        Assert.Equal("InStock", plentyBody.GetProperty("availability").GetString());
        Assert.Equal(JsonValueKind.Null, plentyBody.GetProperty("remaining").ValueKind);
        // The real number appears nowhere in what an anonymous caller is told.
        Assert.DoesNotContain("500", plentyText);
        Assert.DoesNotContain("quantity", plentyText, StringComparison.OrdinalIgnoreCase);

        var noneBody = JsonDocument.Parse(await (await _client.GetAsync($"/inventory/{none}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("OutOfStock", noneBody.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Availability_OfAnUnknownProduct_Is404()
    {
        SignInAs("anonymous");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/inventory/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task ExactQuantities_AreForAdministratorsOnly()
    {
        var productId = await SeedAsync(500);
        var endpoints = new[] { "/inventory", $"/inventory/{productId}/quantity" };

        SignInAs("anonymous");
        foreach (var url in endpoints)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync(url)).StatusCode);

        SignInAs(null);        // signed in, no role
        foreach (var url in endpoints)
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(url)).StatusCode);

        SignInAs("Customer");
        foreach (var url in endpoints)
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync(url)).StatusCode);

        SignInAs("Admin");
        var list = await _client.GetAsync("/inventory");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("500", await list.Content.ReadAsStringAsync());

        var one = await _client.GetAsync($"/inventory/{productId}/quantity");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(500, JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement.GetProperty("quantityAvailable").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/inventory/{Guid.NewGuid()}/quantity")).StatusCode);
    }
}
