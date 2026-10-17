namespace UserService.Application.Users;

/// <summary>
/// The access token (short-lived; sent with every request) and a refresh token (long-lived; used
/// only to get the next pair). Token and ExpiresAtUtc keep their names for existing callers.
/// </summary>
public sealed record LoginResult(string Token, DateTimeOffset ExpiresAtUtc, string RefreshToken, DateTimeOffset RefreshExpiresAtUtc);
