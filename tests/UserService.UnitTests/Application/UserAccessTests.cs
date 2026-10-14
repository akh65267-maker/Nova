using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using UserService.Application;

namespace UserService.UnitTests.Application;

public class UserAccessTests
{
    private static ClaimsPrincipal Caller(Guid? id, string? role = null)
    {
        var claims = new List<Claim>();
        if (id is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, id.Value.ToString()));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void AUserMayReadTheirOwnProfile()
    {
        var me = Guid.NewGuid();

        Assert.True(UserAccess.CanView(Caller(me, "Customer"), me));
    }

    [Fact]
    public void AUserMayNotReadSomeoneElsesProfile()
    {
        Assert.False(UserAccess.CanView(Caller(Guid.NewGuid(), "Customer"), Guid.NewGuid()));
    }

    [Fact]
    public void AnAdminMayReadAnyProfile()
    {
        Assert.True(UserAccess.CanView(Caller(Guid.NewGuid(), "Admin"), Guid.NewGuid()));
    }

    [Fact]
    public void ACallerWithNoSubjectMayReadNothing()
    {
        Assert.False(UserAccess.CanView(Caller(null, "Customer"), Guid.NewGuid()));
    }

    [Fact]
    public void TheComparisonIgnoresTheCaseOfTheId()
    {
        var me = Guid.NewGuid();
        var upper = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, me.ToString().ToUpperInvariant())], "test"));

        Assert.True(UserAccess.CanView(upper, me));
    }
}
