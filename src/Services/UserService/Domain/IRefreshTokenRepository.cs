namespace UserService.Domain;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default);

    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a token used, but only if it has not been used or revoked and has not expired. One
    /// atomic statement, so when the same token is presented twice at once exactly one caller gets
    /// true. Returns whether this call was the one.
    /// </summary>
    Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Ends a sign-in: every token of the family stops working.</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Deletes tokens that expired before <paramref name="expiredBefore"/>. Returns how many.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset expiredBefore, CancellationToken cancellationToken = default);
}
