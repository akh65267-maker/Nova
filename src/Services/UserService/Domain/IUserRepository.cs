using UserService.Application;

namespace UserService.Domain;

public interface IUserRepository
{
    Task<IReadOnlyCollection<User>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default);
    Task AddAsync(User user, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts one wrong password against the account, and locks it when the policy says so. One
    /// atomic statement: concurrent failures each count, none is lost.
    /// </summary>
    Task RegisterFailedLoginAsync(Guid userId, LoginLockoutPolicy policy, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Forgets the account's failures and any lock (after a correct password).</summary>
    Task ClearLoginFailuresAsync(Guid userId, CancellationToken cancellationToken = default);
}
