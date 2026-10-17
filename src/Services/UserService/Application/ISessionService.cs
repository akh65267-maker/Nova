using UserService.Domain;

namespace UserService.Application;

/// <summary>An access token (short-lived, sent with every request) and a refresh token (long-lived, used only to get the next pair).</summary>
public sealed record SessionTokens(string AccessToken, DateTimeOffset AccessExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshExpiresAtUtc);

public interface ISessionService
{
    /// <summary>Starts a sign-in for a user whose password has just been checked.</summary>
    Task<SessionTokens> StartAsync(User user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges a refresh token for a new pair. The presented token is spent: presenting it again
    /// ends the sign-in. Throws <see cref="InvalidRefreshTokenException"/> whatever the reason, so
    /// the answer does not say why.
    /// </summary>
    Task<SessionTokens> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Ends the sign-in a refresh token belongs to. Quiet if the token is unknown or already ended.</summary>
    Task EndAsync(string? refreshToken, CancellationToken cancellationToken = default);
}
