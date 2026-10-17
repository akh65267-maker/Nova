using Contracts.IntegrationEvents;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using UserService.Application;
using UserService.Application.Users;
using UserService.Domain;

namespace UserService.UnitTests.Application;

public class UsersServiceTests
{
    private readonly Mock<IUserRepository> _userRepo = new();
    private readonly Mock<IPasswordHasher> _passwordHasher = new();
    private readonly Mock<IJwtTokenGenerator> _tokenGenerator = new();
    private readonly Mock<IPublishEndpoint> _publishEndpoint = new();
    private readonly UsersService _sut;
    private readonly LoginLockoutPolicy _policy = new(MaxFailures: 5, Window: TimeSpan.FromMinutes(15), Duration: TimeSpan.FromMinutes(15));
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // A user as read from the database with these tracking values (the setters are private
    // because only the repository's atomic statements change them).
    private static User UserWith(int failures = 0, DateTimeOffset? lockedUntil = null, DateTimeOffset? lastFailed = null)
    {
        var user = new User("test@example.com", "Test", "stored-hash");
        typeof(User).GetProperty(nameof(User.FailedLoginCount))!.SetValue(user, failures);
        typeof(User).GetProperty(nameof(User.LockedUntilUtc))!.SetValue(user, lockedUntil);
        typeof(User).GetProperty(nameof(User.LastFailedLoginAtUtc))!.SetValue(user, lastFailed);
        return user;
    }

    public UsersServiceTests()
    {
        _sut = new UsersService(
            _userRepo.Object,
            _passwordHasher.Object,
            _tokenGenerator.Object,
            _publishEndpoint.Object,
            Mock.Of<ILogger<UsersService>>(),
            _policy,
            new FixedClock());

        _passwordHasher.SetupGet(x => x.DummyHash).Returns("dummy-hash");
    }

    [Fact]
    public async Task RegisterAsync_WithDuplicateEmail_ThrowsAndDoesNotPublish()
    {
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed");
        _userRepo.Setup(x => x.EmailExistsAsync("dup@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("dup@example.com", "Someone", "password123")));

        _userRepo.Verify(x => x.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _publishEndpoint.Verify(x => x.Publish(It.IsAny<UserRegistered>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithMissingEmail_ThrowsWithoutCheckingRepo()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("", "Someone", "password123")));

        _userRepo.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithShortPassword_ThrowsWithoutCheckingRepo()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "Someone", "short")));

        _userRepo.Verify(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_WithNewEmail_SavesHashedPasswordAndPublishesUserRegistered()
    {
        _passwordHasher.Setup(x => x.Hash("password123")).Returns("hashed-password");
        _userRepo.Setup(x => x.EmailExistsAsync("new@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var id = await _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "New Person", "password123"));

        Assert.NotEqual(Guid.Empty, id);

        _userRepo.Verify(x => x.AddAsync(
            It.Is<User>(u => u.Email == "new@example.com" && u.Id == id && u.PasswordHash == "hashed-password"),
            It.IsAny<CancellationToken>()), Times.Once);

        _publishEndpoint.Verify(x => x.Publish(
            It.Is<UserRegistered>(e => e.Email == "new@example.com" && e.UserId == id),
            It.IsAny<CancellationToken>()), Times.Once);

        _userRepo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_PublishesBeforeSavingChanges()
    {
        // With the transactional outbox, Publish must be buffered before the
        // single SaveChangesAsync call that commits it — publishing after
        // SaveChangesAsync would mean the message never gets flushed.
        var sequence = new MockSequence();
        _passwordHasher.Setup(x => x.Hash(It.IsAny<string>())).Returns("hashed");
        _userRepo.Setup(x => x.EmailExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _userRepo.InSequence(sequence).Setup(x => x.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _publishEndpoint.InSequence(sequence).Setup(x => x.Publish(It.IsAny<UserRegistered>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _userRepo.InSequence(sequence).Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.RegisterAsync(new RegisterUserRequest("new@example.com", "New Person", "password123"));
    }

    [Fact]
    public async Task LoginAsync_WithUnknownEmail_ThrowsInvalidCredentials()
    {
        _userRepo.Setup(x => x.GetByEmailAsync("missing@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("missing@example.com", "password123")));
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_ThrowsInvalidCredentials()
    {
        var user = new User("test@example.com", "Test", "stored-hash");
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("wrong-password", "stored-hash")).Returns(false);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("test@example.com", "wrong-password")));
    }

    [Fact]
    public async Task LoginAsync_WithCorrectCredentials_ReturnsGeneratedToken()
    {
        var user = new User("test@example.com", "Test", "stored-hash");
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("correct-password", "stored-hash")).Returns(true);
        _tokenGenerator.Setup(x => x.GenerateToken(user)).Returns(("jwt-token", expiresAt));

        var result = await _sut.LoginAsync(new LoginRequest("test@example.com", "correct-password"));

        Assert.Equal("jwt-token", result.Token);
        Assert.Equal(expiresAt, result.ExpiresAtUtc);
    }

    [Fact]
    public async Task LoginAsync_WithUnknownEmail_StillSpendsTheCostOfAVerification()
    {
        _userRepo.Setup(x => x.GetByEmailAsync("missing@example.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("missing@example.com", "password123")));

        // Against the dummy hash: the response time must not tell a caller the address is unknown.
        _passwordHasher.Verify(x => x.Verify("password123", "dummy-hash"), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_WithWrongPassword_CountsTheFailureAgainstTheAccount()
    {
        var user = UserWith();
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("wrong-password", "stored-hash")).Returns(false);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("test@example.com", "wrong-password")));

        _userRepo.Verify(x => x.RegisterFailedLoginAsync(user.Id, _policy, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_ForALockedAccount_RefusesEvenTheCorrectPassword_WithoutCheckingIt()
    {
        var user = UserWith(lockedUntil: Now.AddMinutes(10));
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("correct-password", "stored-hash")).Returns(true);

        await Assert.ThrowsAsync<InvalidCredentialsException>(
            () => _sut.LoginAsync(new LoginRequest("test@example.com", "correct-password")));

        // The real hash is never consulted: a guesser learns nothing about whether a guess was right.
        _passwordHasher.Verify(x => x.Verify(It.IsAny<string>(), "stored-hash"), Times.Never);
        _passwordHasher.Verify(x => x.Verify("correct-password", "dummy-hash"), Times.Once);
        _tokenGenerator.Verify(x => x.GenerateToken(It.IsAny<User>()), Times.Never);
        // Attempts during a lock do not extend it.
        _userRepo.Verify(x => x.RegisterFailedLoginAsync(It.IsAny<Guid>(), It.IsAny<LoginLockoutPolicy>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_WhenTheLockHasExpired_AcceptsTheCorrectPassword_AndClearsTheRecord()
    {
        var user = UserWith(lockedUntil: Now.AddSeconds(-1));
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("correct-password", "stored-hash")).Returns(true);
        _tokenGenerator.Setup(x => x.GenerateToken(user)).Returns(("jwt-token", Now.AddHours(1)));

        var result = await _sut.LoginAsync(new LoginRequest("test@example.com", "correct-password"));

        Assert.Equal("jwt-token", result.Token);
        _userRepo.Verify(x => x.ClearLoginFailuresAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_AfterEarlierFailures_ACorrectPasswordClearsThem()
    {
        var user = UserWith(failures: 3, lastFailed: Now.AddMinutes(-1));
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("correct-password", "stored-hash")).Returns(true);
        _tokenGenerator.Setup(x => x.GenerateToken(user)).Returns(("jwt-token", Now.AddHours(1)));

        await _sut.LoginAsync(new LoginRequest("test@example.com", "correct-password"));

        _userRepo.Verify(x => x.ClearLoginFailuresAsync(user.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_ForACleanAccount_WritesNothingOnSuccess()
    {
        var user = UserWith();
        _userRepo.Setup(x => x.GetByEmailAsync("test@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);
        _passwordHasher.Setup(x => x.Verify("correct-password", "stored-hash")).Returns(true);
        _tokenGenerator.Setup(x => x.GenerateToken(user)).Returns(("jwt-token", Now.AddHours(1)));

        await _sut.LoginAsync(new LoginRequest("test@example.com", "correct-password"));

        _userRepo.Verify(x => x.ClearLoginFailuresAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_GivesTheSameAnswerForUnknown_Wrong_AndLocked()
    {
        var locked = UserWith(lockedUntil: Now.AddMinutes(10));
        var open = UserWith();
        _userRepo.Setup(x => x.GetByEmailAsync("locked@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(locked);
        _userRepo.Setup(x => x.GetByEmailAsync("open@example.com", It.IsAny<CancellationToken>())).ReturnsAsync(open);
        _userRepo.Setup(x => x.GetByEmailAsync("missing@example.com", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        _passwordHasher.Setup(x => x.Verify(It.IsAny<string>(), "stored-hash")).Returns(false);

        var messages = new List<string>();
        foreach (var email in new[] { "locked@example.com", "open@example.com", "missing@example.com" })
        {
            var error = await Assert.ThrowsAsync<InvalidCredentialsException>(
                () => _sut.LoginAsync(new LoginRequest(email, "whatever-password")));
            messages.Add(error.Message);
        }

        // A caller must not be able to tell the three apart, or lockout becomes a way to find accounts.
        Assert.Single(messages.Distinct());
    }
}
