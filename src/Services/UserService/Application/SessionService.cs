using System.Security.Cryptography;
using System.Text;
using UserService.Domain;

namespace UserService.Application;

public class SessionService : ISessionService
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly IUserRepository _users;
    private readonly IJwtTokenGenerator _jwt;
    private readonly RefreshTokenOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionService> _logger;

    public SessionService(
        IRefreshTokenRepository tokens,
        IUserRepository users,
        IJwtTokenGenerator jwt,
        RefreshTokenOptions options,
        TimeProvider clock,
        ILogger<SessionService> logger)
    {
        _tokens = tokens;
        _users = users;
        _jwt = jwt;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public Task<SessionTokens> StartAsync(User user, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        return IssueAsync(user, familyId: Guid.NewGuid(), familyCreatedAtUtc: now, now, cancellationToken);
    }

    public async Task<SessionTokens> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidRefreshTokenException();

        var now = _clock.GetUtcNow();
        var stored = await _tokens.FindByHashAsync(Hash(refreshToken), cancellationToken);

        if (stored is null || stored.RevokedAtUtc is not null || stored.ExpiresAtUtc <= now)
            throw new InvalidRefreshTokenException();

        if (stored.UsedAtUtc is { } usedAt)
        {
            // Already exchanged once. Moments later, that is two tabs racing; later than that, two
            // parties hold the same token and one of them did not sign in, so the sign-in ends.
            if (now - usedAt > TimeSpan.FromSeconds(_options.ReuseGraceSeconds))
            {
                await _tokens.RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
                _logger.LogWarning("Refresh token reused after {Seconds} s; ended sign-in family {FamilyId} of user {UserId}",
                    (int)(now - usedAt).TotalSeconds, stored.FamilyId, stored.UserId);
            }

            throw new InvalidRefreshTokenException();
        }

        var user = await _users.GetByIdAsync(stored.UserId, cancellationToken);
        if (user is null)
        {
            await _tokens.RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            throw new InvalidRefreshTokenException();
        }

        // The spending is one atomic statement: of two callers presenting this token at once, one wins.
        if (!await _tokens.TryMarkUsedAsync(stored.Id, now, cancellationToken))
            throw new InvalidRefreshTokenException();

        return await IssueAsync(user, stored.FamilyId, stored.FamilyCreatedAtUtc, now, cancellationToken);
    }

    public async Task EndAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return;

        var stored = await _tokens.FindByHashAsync(Hash(refreshToken), cancellationToken);
        if (stored is null)
            return;

        await _tokens.RevokeFamilyAsync(stored.FamilyId, _clock.GetUtcNow(), cancellationToken);
    }

    private async Task<SessionTokens> IssueAsync(
        User user, Guid familyId, DateTimeOffset familyCreatedAtUtc, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Good for the sliding period, but never past the sign-in's absolute lifetime.
        var expiresAtUtc = Min(now.AddDays(_options.RefreshSlidingDays), familyCreatedAtUtc.AddDays(_options.RefreshAbsoluteDays));

        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        await _tokens.AddAsync(new RefreshToken(user.Id, familyId, Hash(raw), familyCreatedAtUtc, now, expiresAtUtc), cancellationToken);

        var (accessToken, accessExpiresAtUtc) = _jwt.GenerateToken(user);
        return new SessionTokens(accessToken, accessExpiresAtUtc, raw, expiresAtUtc);
    }

    /// <summary>SHA-256 as hex. The tokens are 256 random bits, so a plain hash (no salt, no stretching) is enough: there is nothing to guess.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
