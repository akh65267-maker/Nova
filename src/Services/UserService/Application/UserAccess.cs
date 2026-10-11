using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using UserService.Domain;

namespace UserService.Application;

/// <summary>Who may read which user's profile.</summary>
public static class UserAccess
{
    /// <summary>
    /// A user may read their own profile; an admin may read anyone's. Anyone else may not, and
    /// the endpoint answers 404 rather than 403 so that it does not confirm which ids exist
    /// (the same choice OrderService makes for another user's order). Ids are not secret:
    /// they appear in tokens, orders and responses, so "nobody will guess it" is not the rule.
    /// </summary>
    public static bool CanView(ClaimsPrincipal caller, Guid userId) =>
        caller.IsInRole(nameof(UserRole.Admin))
        || string.Equals(
            caller.FindFirstValue(JwtRegisteredClaimNames.Sub),
            userId.ToString(),
            StringComparison.OrdinalIgnoreCase);
}
