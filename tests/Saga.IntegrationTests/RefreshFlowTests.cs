extern alias UserAlias;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

using UserDbContext = UserAlias::UserService.Infrastructure.Persistence.UserDbContext;

namespace Saga.IntegrationTests;

/// <summary>
/// Sign in, refresh, reuse and sign out against UserService over real HTTP and a real Postgres.
/// </summary>
public class RefreshFlowTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private WebApplicationFactory<UserAlias::Program> _factory = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        var options = new DbContextOptionsBuilder<UserDbContext>().UseNpgsql(_db.GetConnectionString()).Options;
        await using (var context = new UserDbContext(options))
            await context.Database.MigrateAsync();

        _factory = new WebApplicationFactory<UserAlias::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:UserDatabase", _db.GetConnectionString());
            // An address that cannot resolve: these endpoints never touch the broker.
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

    private sealed record Tokens(string Token, string RefreshToken, DateTimeOffset ExpiresAtUtc, DateTimeOffset RefreshExpiresAtUtc);

    private async Task<(string Email, Guid Id)> RegisterAsync()
    {
        var email = $"flow+{Guid.NewGuid():N}@example.com";
        var response = await _client.PostAsJsonAsync("/users", new { email, displayName = "Flow", password = "Right-pass-123" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        return (email, id);
    }

    private async Task<Tokens> LoginAsync(string email)
    {
        var response = await _client.PostAsJsonAsync("/users/login", new { email, password = "Right-pass-123" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Tokens>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string? refreshToken) =>
        _client.PostAsJsonAsync("/users/refresh", new { refreshToken });

    private async Task<Tokens> RefreshOkAsync(string refreshToken)
    {
        var response = await RefreshAsync(refreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Tokens>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private async Task ExecuteSqlAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountTokensAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"RefreshTokens\" WHERE \"UserId\" = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task SigningIn_ReturnsAnAccessTokenAndARefreshToken()
    {
        var (email, id) = await RegisterAsync();

        var tokens = await LoginAsync(email);

        Assert.False(string.IsNullOrEmpty(tokens.Token));
        Assert.True(tokens.RefreshToken.Length >= 40);
        Assert.True(tokens.RefreshExpiresAtUtc > tokens.ExpiresAtUtc);   // the refresh token outlives the access token
        Assert.Equal(1, await CountTokensAsync(id));
    }

    [Fact]
    public async Task AFailedSignIn_IssuesNoRefreshToken()
    {
        var (email, id) = await RegisterAsync();

        var response = await _client.PostAsJsonAsync("/users/login", new { email, password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountTokensAsync(id));
    }

    [Fact]
    public async Task Refreshing_GivesANewPair_AndTheNewAccessTokenWorks()
    {
        var (email, id) = await RegisterAsync();
        var first = await LoginAsync(email);

        var second = await RefreshOkAsync(first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.Token, second.Token);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Token);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/users/{id}")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [Fact]
    public async Task ASpentToken_IsRefused_ButAMomentLaterTheNewOneStillWorks()
    {
        var (email, _) = await RegisterAsync();
        var first = await LoginAsync(email);
        var second = await RefreshOkAsync(first.RefreshToken);

        // Straight away: that is an ordinary race between two tabs, not theft.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken)).StatusCode);

        // So the sign-in goes on.
        var third = await RefreshOkAsync(second.RefreshToken);
        Assert.NotEqual(second.RefreshToken, third.RefreshToken);
    }

    [Fact]
    public async Task ASpentTokenTurningUpLater_EndsTheWholeSignIn()
    {
        var (email, id) = await RegisterAsync();
        var first = await LoginAsync(email);
        var second = await RefreshOkAsync(first.RefreshToken);

        // Make the first token's use look like it happened five minutes ago.
        await ExecuteSqlAsync("UPDATE \"RefreshTokens\" SET \"UsedAtUtc\" = now() - interval '5 minutes' WHERE \"UserId\" = @id AND \"UsedAtUtc\" IS NOT NULL", ("id", id));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken)).StatusCode);

        // The legitimate holder of the newest token is signed out too: someone else had the old one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(second.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task SigningOut_EndsTheSignIn_AndDoingItAgainIsQuiet()
    {
        var (email, _) = await RegisterAsync();
        var tokens = await LoginAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/users/logout", new { refreshToken = tokens.RefreshToken })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(tokens.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/users/logout", new { refreshToken = tokens.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/users/logout", new { refreshToken = "never-issued" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/users/logout", new { refreshToken = (string?)null })).StatusCode);
    }

    [Fact]
    public async Task SigningOutOnOneDevice_LeavesTheOthersSignedIn()
    {
        var (email, _) = await RegisterAsync();
        var phone = await LoginAsync(email);
        var laptop = await LoginAsync(email);

        await _client.PostAsJsonAsync("/users/logout", new { refreshToken = phone.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(laptop.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task TheAccessTokenAlreadyIssued_StillWorksAfterSigningOut_UntilItExpires()
    {
        // The honest limit of this design, pinned so nobody assumes otherwise: signing out ends
        // the refresh token at once, but an access token is checked by every service on its own
        // and carries no revocation, so it lives until it expires (see docs/authentication.md).
        var (email, id) = await RegisterAsync();
        var tokens = await LoginAsync(email);
        await _client.PostAsJsonAsync("/users/logout", new { refreshToken = tokens.RefreshToken });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Token);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/users/{id}")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task Refreshing_WithNothingOrAnUnknownToken_IsRefusedWith401(string? refreshToken)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(refreshToken)).StatusCode);
    }

    [Fact]
    public async Task RefreshingAfterTheTokenExpired_IsRefused()
    {
        var (email, id) = await RegisterAsync();
        var tokens = await LoginAsync(email);
        await ExecuteSqlAsync("UPDATE \"RefreshTokens\" SET \"ExpiresAtUtc\" = now() - interval '1 minute' WHERE \"UserId\" = @id", ("id", id));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task TheStoredTokenIsAHash_NotTheTokenTheClientHolds()
    {
        var (email, id) = await RegisterAsync();
        var tokens = await LoginAsync(email);

        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"TokenHash\" FROM \"RefreshTokens\" WHERE \"UserId\" = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var stored = (string)(await command.ExecuteScalarAsync())!;

        Assert.NotEqual(tokens.RefreshToken, stored);
        Assert.DoesNotContain(tokens.RefreshToken, stored);
        Assert.Equal(64, stored.Length);
    }
}
