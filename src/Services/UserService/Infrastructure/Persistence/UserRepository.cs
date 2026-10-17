using Microsoft.EntityFrameworkCore;
using UserService.Application;
using UserService.Domain;

namespace UserService.Infrastructure.Persistence;

public class UserRepository : IUserRepository
{
    private readonly UserDbContext _db;

    public UserRepository(UserDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<User>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default)
    {
        return await _db.Users
            .AsNoTracking()
            .AnyAsync(x => x.Email == email, cancellationToken);
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        await _db.Users.AddAsync(user, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RegisterFailedLoginAsync(
        Guid userId, LoginLockoutPolicy policy, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var windowStart = now - policy.Window;
        var lockedUntil = now + policy.Duration;

        // One statement, so the row is locked while it runs and every concurrent failure counts
        // (a read-then-write here would let parallel guesses all see the same count). A failure
        // more than Window after the previous one starts the count again at 1. Reaching the limit
        // sets the lock and zeroes the count, so the first failure after the lock expires counts
        // as 1 rather than re-locking at once. Every column on the right of an assignment is the
        // row's value from BEFORE this statement, which is what the CASE expressions rely on.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Users" SET
                "FailedLoginCount" = CASE
                    WHEN (CASE WHEN "LastFailedLoginAtUtc" IS NULL OR "LastFailedLoginAtUtc" < {windowStart}
                               THEN 1 ELSE "FailedLoginCount" + 1 END) >= {policy.MaxFailures}
                    THEN 0
                    WHEN "LastFailedLoginAtUtc" IS NULL OR "LastFailedLoginAtUtc" < {windowStart}
                    THEN 1
                    ELSE "FailedLoginCount" + 1 END,
                "LastFailedLoginAtUtc" = CASE
                    WHEN (CASE WHEN "LastFailedLoginAtUtc" IS NULL OR "LastFailedLoginAtUtc" < {windowStart}
                               THEN 1 ELSE "FailedLoginCount" + 1 END) >= {policy.MaxFailures}
                    THEN NULL ELSE {now} END,
                "LockedUntilUtc" = CASE
                    WHEN (CASE WHEN "LastFailedLoginAtUtc" IS NULL OR "LastFailedLoginAtUtc" < {windowStart}
                               THEN 1 ELSE "FailedLoginCount" + 1 END) >= {policy.MaxFailures}
                    THEN {lockedUntil} ELSE "LockedUntilUtc" END
            WHERE "Id" = {userId}
            """, cancellationToken);
    }

    public async Task ClearLoginFailuresAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await _db.Users
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.FailedLoginCount, 0)
                .SetProperty(u => u.LastFailedLoginAtUtc, (DateTimeOffset?)null)
                .SetProperty(u => u.LockedUntilUtc, (DateTimeOffset?)null), cancellationToken);
    }
}
