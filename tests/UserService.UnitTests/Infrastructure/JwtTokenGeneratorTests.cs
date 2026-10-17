using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using UserService.Domain;
using UserService.Infrastructure.Security;

namespace UserService.UnitTests.Infrastructure;

public class JwtTokenGeneratorTests
{
    private static JwtTokenGenerator Create(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 48) };
        foreach (var (key, value) in extra)
            settings[key] = value;
        return new JwtTokenGenerator(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static User AUser() => new("a@example.com", "A User", "hash");

    [Fact]
    public void GenerateToken_ByDefault_LivesFifteenMinutes()
    {
        var before = DateTimeOffset.UtcNow;

        var (token, expiresAtUtc) = Create().GenerateToken(AUser());

        Assert.InRange(expiresAtUtc, before.AddMinutes(15), DateTimeOffset.UtcNow.AddMinutes(15));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(expiresAtUtc.UtcDateTime, jwt.ValidTo, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void GenerateToken_WithConfiguredMinutes_UsesThem()
    {
        var before = DateTimeOffset.UtcNow;

        var (_, expiresAtUtc) = Create(("Jwt:AccessTokenMinutes", "60")).GenerateToken(AUser());

        Assert.InRange(expiresAtUtc, before.AddMinutes(60), DateTimeOffset.UtcNow.AddMinutes(60));
    }
}
