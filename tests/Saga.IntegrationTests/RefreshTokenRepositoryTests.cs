extern alias UserAlias;

using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

using RefreshToken = UserAlias::UserService.Domain.RefreshToken;
using RefreshTokenRepository = UserAlias::UserService.Infrastructure.Persistence.RefreshTokenRepository;
using User = UserAlias::UserService.Domain.User;
using UserDbContext = UserAlias::UserService.Infrastructure.Persistence.UserDbContext;

namespace Saga.IntegrationTests;

/// <summary>
/// The refresh-token store against a real Postgres. What matters most is that a token can be
/// spent exactly once even when it is presented twice at the same moment, which only a real
/// database can show.
/// </summary>
public class RefreshTokenRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

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
        var user = new User($"refresh+{Guid.NewGuid():N}@example.com", "Refresh Test", "hash");
        await using var context = new UserDbContext(_options);
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private async Task<RefreshToken> AddTokenAsync(Guid userId, Guid? family = null, DateTimeOffset? expires = null, string? hash = null)
    {
        var token = new RefreshToken(userId, family ?? Guid.NewGuid(), hash ?? Guid.NewGuid().ToString("N").PadRight(64, '0'), Now, Now, expires ?? Now.AddDays(14));
        await using var context = new UserDbContext(_options);
        await new RefreshTokenRepository(context).AddAsync(token);
        return token;
    }

    private async Task<RefreshToken> ReadAsync(Guid id)
    {
        await using var context = new UserDbContext(_options);
        return await context.RefreshTokens.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset at)
    {
        await using var context = new UserDbContext(_options);
        return await new RefreshTokenRepository(context).TryMarkUsedAsync(id, at);
    }

    [Fact]
    public async Task ATokenCanBeFoundByItsHash()
    {
        var token = await AddTokenAsync(await AddUserAsync(), hash: new string('a', 64));

        await using var context = new UserDbContext(_options);
        var found = await new RefreshTokenRepository(context).FindByHashAsync(new string('a', 64));

        Assert.Equal(token.Id, found!.Id);
        Assert.Null(await new RefreshTokenRepository(context).FindByHashAsync(new string('b', 64)));
    }

    [Fact]
    public async Task TwoTokensCannotShareAHash()
    {
        var userId = await AddUserAsync();
        await AddTokenAsync(userId, hash: new string('c', 64));

        await Assert.ThrowsAsync<DbUpdateException>(() => AddTokenAsync(userId, hash: new string('c', 64)));
    }

    [Fact]
    public async Task ATokenIsSpentOnce_AndASecondAttemptFails()
    {
        var token = await AddTokenAsync(await AddUserAsync());

        Assert.True(await TryMarkUsedAsync(token.Id, Now));
        Assert.False(await TryMarkUsedAsync(token.Id, Now.AddSeconds(1)));
        Assert.Equal(Now, (await ReadAsync(token.Id)).UsedAtUtc);   // the first use is what is recorded
    }

    [Fact]
    public async Task TheSameTokenPresentedTwentyTimesAtOnce_IsSpentByExactlyOneCaller()
    {
        var token = await AddTokenAsync(await AddUserAsync());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => TryMarkUsedAsync(token.Id, Now)));

        // A read-then-write would let several of them win, and each would be issued a new token.
        Assert.Single(results, won => won);
    }

    [Fact]
    public async Task AnExpiredToken_CannotBeSpent()
    {
        var token = await AddTokenAsync(await AddUserAsync(), expires: Now.AddSeconds(-1));

        Assert.False(await TryMarkUsedAsync(token.Id, Now));
    }

    [Fact]
    public async Task ARevokedToken_CannotBeSpent()
    {
        var token = await AddTokenAsync(await AddUserAsync());
        await using (var context = new UserDbContext(_options))
            await new RefreshTokenRepository(context).RevokeFamilyAsync(token.FamilyId, Now);

        Assert.False(await TryMarkUsedAsync(token.Id, Now));
    }

    [Fact]
    public async Task RevokingAFamily_EndsEveryTokenInIt_AndNoOtherFamily()
    {
        var userId = await AddUserAsync();
        var family = Guid.NewGuid();
        var first = await AddTokenAsync(userId, family);
        var second = await AddTokenAsync(userId, family);
        var other = await AddTokenAsync(userId);

        await using (var context = new UserDbContext(_options))
            await new RefreshTokenRepository(context).RevokeFamilyAsync(family, Now);

        Assert.NotNull((await ReadAsync(first.Id)).RevokedAtUtc);
        Assert.NotNull((await ReadAsync(second.Id)).RevokedAtUtc);
        Assert.Null((await ReadAsync(other.Id)).RevokedAtUtc);
    }

    [Fact]
    public async Task Cleanup_DeletesOnlyTokensExpiredBeforeTheCutoff()
    {
        var userId = await AddUserAsync();
        var longGone = await AddTokenAsync(userId, expires: Now.AddDays(-10));
        var recentlyExpired = await AddTokenAsync(userId, expires: Now.AddHours(-1));
        var live = await AddTokenAsync(userId, expires: Now.AddDays(5));

        int deleted;
        await using (var context = new UserDbContext(_options))
            deleted = await new RefreshTokenRepository(context).DeleteExpiredAsync(Now.AddDays(-1));

        Assert.Equal(1, deleted);
        await using var check = new UserDbContext(_options);
        Assert.False(await check.RefreshTokens.AnyAsync(t => t.Id == longGone.Id));
        Assert.True(await check.RefreshTokens.AnyAsync(t => t.Id == recentlyExpired.Id));
        Assert.True(await check.RefreshTokens.AnyAsync(t => t.Id == live.Id));
    }

    [Fact]
    public async Task DeletingAUser_DeletesTheirTokens()
    {
        var userId = await AddUserAsync();
        var token = await AddTokenAsync(userId);

        await using (var context = new UserDbContext(_options))
            await context.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();

        await using var check = new UserDbContext(_options);
        Assert.False(await check.RefreshTokens.AnyAsync(t => t.Id == token.Id));
    }
}
