using Microsoft.Extensions.Logging;
using Moq;
using UserService.Application;
using UserService.Domain;

namespace UserService.UnitTests.Application;

public class SessionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IRefreshTokenRepository> _tokens = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IJwtTokenGenerator> _jwt = new();
    private readonly RefreshTokenOptions _options = new(RefreshSlidingDays: 14, RefreshAbsoluteDays: 30, ReuseGraceSeconds: 10);
    private readonly User _user = new("person@example.com", "Person", "stored-hash");
    private DateTimeOffset _now = Now;

    private sealed class MovableClock : TimeProvider
    {
        private readonly Func<DateTimeOffset> _now;
        public MovableClock(Func<DateTimeOffset> now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now();
    }

    public SessionServiceTests()
    {
        _jwt.Setup(x => x.GenerateToken(It.IsAny<User>())).Returns(("access-token", Now.AddMinutes(15)));
        _users.Setup(x => x.GetByIdAsync(_user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_user);
    }

    private SessionService Sut() =>
        new(_tokens.Object, _users.Object, _jwt.Object, _options, new MovableClock(() => _now), Mock.Of<ILogger<SessionService>>());

    // The token as the repository would hand it back for a given raw value.
    private RefreshToken Stored(
        string raw, DateTimeOffset? familyCreated = null, DateTimeOffset? expires = null, DateTimeOffset? used = null, DateTimeOffset? revoked = null)
    {
        var token = new RefreshToken(_user.Id, Guid.NewGuid(), SessionService.Hash(raw), familyCreated ?? Now.AddDays(-1), Now.AddDays(-1), expires ?? Now.AddDays(13));
        typeof(RefreshToken).GetProperty(nameof(RefreshToken.UsedAtUtc))!.SetValue(token, used);
        typeof(RefreshToken).GetProperty(nameof(RefreshToken.RevokedAtUtc))!.SetValue(token, revoked);
        _tokens.Setup(x => x.FindByHashAsync(SessionService.Hash(raw), It.IsAny<CancellationToken>())).ReturnsAsync(token);
        return token;
    }

    [Fact]
    public async Task Start_IssuesATokenPair_AndStoresOnlyTheHashOfTheRefreshToken()
    {
        RefreshToken? saved = null;
        _tokens.Setup(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()))
            .Callback<RefreshToken, CancellationToken>((t, _) => saved = t).Returns(Task.CompletedTask);

        var pair = await Sut().StartAsync(_user);

        Assert.Equal("access-token", pair.AccessToken);
        Assert.NotNull(saved);
        Assert.Equal(_user.Id, saved!.UserId);
        Assert.Equal(SessionService.Hash(pair.RefreshToken), saved.TokenHash);
        Assert.NotEqual(pair.RefreshToken, saved.TokenHash);          // the token itself is not what is stored
        Assert.Equal(Now.AddDays(14), saved.ExpiresAtUtc);
        Assert.Equal(Now.AddDays(14), pair.RefreshExpiresAtUtc);
        Assert.True(pair.RefreshToken.Length >= 40);                  // 256 bits, not guessable
    }

    [Fact]
    public async Task Start_GivesEachSignInItsOwnTokenAndFamily()
    {
        var saved = new List<RefreshToken>();
        _tokens.Setup(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()))
            .Callback<RefreshToken, CancellationToken>((t, _) => saved.Add(t)).Returns(Task.CompletedTask);

        var first = await Sut().StartAsync(_user);
        var second = await Sut().StartAsync(_user);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(saved[0].FamilyId, saved[1].FamilyId);
    }

    [Fact]
    public async Task Refresh_ExchangesTheTokenForANewPair_InTheSameFamily()
    {
        var old = Stored("old-token");
        _tokens.Setup(x => x.TryMarkUsedAsync(old.Id, Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        RefreshToken? next = null;
        _tokens.Setup(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()))
            .Callback<RefreshToken, CancellationToken>((t, _) => next = t).Returns(Task.CompletedTask);

        var pair = await Sut().RefreshAsync("old-token");

        Assert.Equal("access-token", pair.AccessToken);
        Assert.NotEqual("old-token", pair.RefreshToken);
        Assert.Equal(old.FamilyId, next!.FamilyId);
        Assert.Equal(old.FamilyCreatedAtUtc, next.FamilyCreatedAtUtc);
        _tokens.Verify(x => x.TryMarkUsedAsync(old.Id, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_BuildsTheNewAccessTokenFromTheUserAsTheyAreNow()
    {
        var old = Stored("old-token");
        _tokens.Setup(x => x.TryMarkUsedAsync(old.Id, Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Sut().RefreshAsync("old-token");

        // So a changed role takes effect at the next refresh, not at the next sign-in.
        _jwt.Verify(x => x.GenerateToken(_user), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refresh_WithNoToken_IsRefused(string? raw)
    {
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync(raw));
    }

    [Fact]
    public async Task Refresh_WithAnUnknownToken_IsRefused()
    {
        _tokens.Setup(x => x.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((RefreshToken?)null);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("never-issued"));

        _tokens.Verify(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_WithAnExpiredToken_IsRefused()
    {
        Stored("old-token", expires: Now.AddSeconds(-1));

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("old-token"));

        _tokens.Verify(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_WithATokenOfAnEndedSignIn_IsRefused()
    {
        Stored("old-token", revoked: Now.AddMinutes(-5));

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("old-token"));

        _tokens.Verify(x => x.TryMarkUsedAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_WithATokenUsedLongAgo_EndsTheWholeSignIn()
    {
        // A token that was already exchanged turning up again means two parties hold it: one of
        // them is not the person who signed in. Ending the sign-in costs the real person one login.
        var old = Stored("old-token", used: Now.AddMinutes(-5));

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("old-token"));

        _tokens.Verify(x => x.RevokeFamilyAsync(old.FamilyId, Now, It.IsAny<CancellationToken>()), Times.Once);
        _tokens.Verify(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_WithATokenUsedAMomentAgo_IsRefusedButDoesNotEndTheSignIn()
    {
        // Two tabs refreshing at once is an ordinary race, not theft.
        var old = Stored("old-token", used: Now.AddSeconds(-3));

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("old-token"));

        _tokens.Verify(x => x.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _tokens.Verify(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(old);
    }

    [Fact]
    public async Task Refresh_WhenAnotherCallerSpendsTheTokenFirst_IsRefusedAndIssuesNothing()
    {
        var old = Stored("old-token");
        _tokens.Setup(x => x.TryMarkUsedAsync(old.Id, Now, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("old-token"));

        _tokens.Verify(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_ForAUserWhoNoLongerExists_IsRefused_AndEndsTheSignIn()
    {
        var old = Stored("old-token");
        _users.Setup(x => x.GetByIdAsync(_user.Id, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => Sut().RefreshAsync("old-token"));

        _tokens.Verify(x => x.RevokeFamilyAsync(old.FamilyId, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_NeverExtendsASignInPastItsAbsoluteLifetime()
    {
        // The sign-in started 29 days ago; a fresh 14-day token would outlive the 30-day limit.
        var old = Stored("old-token", familyCreated: Now.AddDays(-29));
        _tokens.Setup(x => x.TryMarkUsedAsync(old.Id, Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var pair = await Sut().RefreshAsync("old-token");

        Assert.Equal(Now.AddDays(1), pair.RefreshExpiresAtUtc);
    }

    [Fact]
    public async Task End_RevokesTheWholeFamily()
    {
        var old = Stored("some-token");

        await Sut().EndAsync("some-token");

        _tokens.Verify(x => x.RevokeFamilyAsync(old.FamilyId, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task End_IsQuietForNothingOrAnUnknownToken(string? raw)
    {
        _tokens.Setup(x => x.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((RefreshToken?)null);

        await Sut().EndAsync(raw);   // no exception: signing out twice, or with a stale cookie, is not an error

        _tokens.Verify(x => x.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Hash_IsStable_AndDifferentForDifferentTokens()
    {
        Assert.Equal(SessionService.Hash("a-token"), SessionService.Hash("a-token"));
        Assert.NotEqual(SessionService.Hash("a-token"), SessionService.Hash("another-token"));
        Assert.Equal(64, SessionService.Hash("a-token").Length);   // SHA-256 as hex
    }
}
