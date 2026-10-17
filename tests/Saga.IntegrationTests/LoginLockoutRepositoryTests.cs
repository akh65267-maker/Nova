extern alias UserAlias;

using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

using LoginLockoutPolicy = UserAlias::UserService.Application.LoginLockoutPolicy;
using User = UserAlias::UserService.Domain.User;
using UserDbContext = UserAlias::UserService.Infrastructure.Persistence.UserDbContext;
using UserRepository = UserAlias::UserService.Infrastructure.Persistence.UserRepository;

namespace Saga.IntegrationTests;

/// <summary>
/// The wrong-password counter, against a real Postgres. The point of doing it in one SQL statement
/// is that parallel guesses cannot slip past the counter, which only a real database can show.
/// </summary>
public class LoginLockoutRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly LoginLockoutPolicy Policy = new(MaxFailures: 5, Window: TimeSpan.FromMinutes(15), Duration: TimeSpan.FromMinutes(15));

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private DbContextOptions<UserDbContext> _options = default!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _options = new DbContextOptionsBuilder<UserDbContext>().UseNpgsql(_db.GetConnectionString()).Options;
        await using var context = new UserDbContext(_options);
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task<Guid> AddUserAsync()
    {
        var user = new User($"lockout+{Guid.NewGuid():N}@example.com", "Lockout Test", "hash");
        await using var context = new UserDbContext(_options);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private async Task FailAsync(Guid id, DateTimeOffset at, LoginLockoutPolicy? policy = null)
    {
        await using var context = new UserDbContext(_options);
        await new UserRepository(context).RegisterFailedLoginAsync(id, policy ?? Policy, at);
    }

    private async Task<User> ReadAsync(Guid id)
    {
        await using var context = new UserDbContext(_options);
        return await context.Users.AsNoTracking().SingleAsync(u => u.Id == id);
    }

    [Fact]
    public async Task FailuresBelowTheLimit_AreCounted_AndDoNotLock()
    {
        var id = await AddUserAsync();

        for (var i = 0; i < 4; i++) await FailAsync(id, Now.AddSeconds(i));

        var user = await ReadAsync(id);
        Assert.Equal(4, user.FailedLoginCount);
        Assert.Null(user.LockedUntilUtc);
        Assert.False(user.IsLockedAt(Now.AddSeconds(5)));
    }

    [Fact]
    public async Task TheFailureThatReachesTheLimit_LocksTheAccountForTheDuration_AndResetsTheCount()
    {
        var id = await AddUserAsync();

        for (var i = 0; i < 5; i++) await FailAsync(id, Now);

        var user = await ReadAsync(id);
        Assert.Equal(Now.AddMinutes(15), user.LockedUntilUtc);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LastFailedLoginAtUtc);
        Assert.True(user.IsLockedAt(Now.AddMinutes(14)));
        Assert.False(user.IsLockedAt(Now.AddMinutes(15)));
    }

    [Fact]
    public async Task AFailureLongAfterTheLast_StartsTheCountAgain()
    {
        var id = await AddUserAsync();
        for (var i = 0; i < 4; i++) await FailAsync(id, Now);

        // 16 minutes later: the four earlier failures are outside the window.
        await FailAsync(id, Now.AddMinutes(16));

        Assert.Equal(1, (await ReadAsync(id)).FailedLoginCount);
    }

    [Fact]
    public async Task TheFirstFailureAfterALockExpires_CountsAsOne_AndDoesNotLockAgain()
    {
        var id = await AddUserAsync();
        for (var i = 0; i < 5; i++) await FailAsync(id, Now);

        await FailAsync(id, Now.AddMinutes(16));

        var user = await ReadAsync(id);
        Assert.Equal(1, user.FailedLoginCount);
        Assert.False(user.IsLockedAt(Now.AddMinutes(16)));
    }

    [Fact]
    public async Task Clearing_ForgetsTheFailuresAndTheLock()
    {
        var id = await AddUserAsync();
        for (var i = 0; i < 5; i++) await FailAsync(id, Now);

        await using (var context = new UserDbContext(_options))
            await new UserRepository(context).ClearLoginFailuresAsync(id);

        var user = await ReadAsync(id);
        Assert.False(user.HasLoginFailures);
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntilUtc);
    }

    [Fact]
    public async Task ParallelFailures_AreAllCounted_NoneIsLost()
    {
        var id = await AddUserAsync();
        var generous = new LoginLockoutPolicy(MaxFailures: 1000, Window: TimeSpan.FromMinutes(15), Duration: TimeSpan.FromMinutes(15));

        await Task.WhenAll(Enumerable.Range(0, 60).Select(_ => FailAsync(id, Now, generous)));

        // A read-modify-write would have lost most of these: each would read the same count.
        Assert.Equal(60, (await ReadAsync(id)).FailedLoginCount);
    }

    [Fact]
    public async Task ParallelFailures_PastTheLimit_LeaveTheAccountLocked()
    {
        var id = await AddUserAsync();

        await Task.WhenAll(Enumerable.Range(0, 23).Select(_ => FailAsync(id, Now)));

        var user = await ReadAsync(id);
        Assert.True(user.IsLockedAt(Now.AddMinutes(1)));
        // 23 failures with a limit of 5: four locks, three counted since the last one.
        Assert.Equal(3, user.FailedLoginCount);
    }

    [Fact]
    public async Task AFailureForAnAccountThatDoesNotExist_ChangesNothingAndDoesNotThrow()
    {
        await FailAsync(Guid.NewGuid(), Now);
    }
}
