using Microsoft.EntityFrameworkCore;
using UserService.Domain;

namespace UserService.Infrastructure.Persistence;

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly UserDbContext _db;

    public RefreshTokenRepository(UserDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        return await _db.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
    }

    public async Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        // One conditional UPDATE: the row is locked while it runs, so of two callers presenting the
        // same token at the same moment, exactly one sees it unused and changes it.
        var changed = await _db.RefreshTokens
            .Where(t => t.Id == id && t.UsedAtUtc == null && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAtUtc, (DateTimeOffset?)now), cancellationToken);

        return changed == 1;
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAtUtc, (DateTimeOffset?)now), cancellationToken);
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset expiredBefore, CancellationToken cancellationToken = default)
    {
        return await _db.RefreshTokens
            .Where(t => t.ExpiresAtUtc < expiredBefore)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
