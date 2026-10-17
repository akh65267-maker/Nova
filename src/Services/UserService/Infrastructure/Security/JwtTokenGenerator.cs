using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UserService.Application;
using UserService.Domain;

namespace UserService.Infrastructure.Security;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IConfiguration _configuration;

    public JwtTokenGenerator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public (string Token, DateTimeOffset ExpiresAtUtc) GenerateToken(User user)
    {
        var key = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key configuration is missing.");
        var issuer = _configuration["Jwt:Issuer"] ?? "TaskManager";
        var audience = _configuration["Jwt:Audience"] ?? "TaskManager";

        // How long an access token lives. The default is the original hour. The point of refresh
        // tokens is to make this short (15 minutes), which the apps do once they refresh by themselves.
        var minutes = _configuration.GetValue("Jwt:AccessTokenMinutes", 60);
        if (minutes < 1)
            throw new InvalidOperationException("Jwt:AccessTokenMinutes must be at least 1.");

        var expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(minutes);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAtUtc.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }
}
