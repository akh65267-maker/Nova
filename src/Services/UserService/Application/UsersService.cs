using Contracts.IntegrationEvents;
using MassTransit;
using UserService.Application.Users;
using UserService.Domain;

namespace UserService.Application;

public class UsersService
{
    private readonly IUserRepository _repo;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ISessionService _sessions;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<UsersService> _logger;
    private readonly LoginLockoutPolicy _lockout;
    private readonly TimeProvider _clock;

    public UsersService(
        IUserRepository repo,
        IPasswordHasher passwordHasher,
        ISessionService sessions,
        IPublishEndpoint publishEndpoint,
        ILogger<UsersService> logger,
        LoginLockoutPolicy lockout,
        TimeProvider clock)
    {
        _repo = repo;
        _passwordHasher = passwordHasher;
        _sessions = sessions;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
        _lockout = lockout;
        _clock = clock;
    }

    public async Task<IReadOnlyCollection<UserDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var users = await _repo.GetAllAsync(cancellationToken);

        return users.Select(user => new UserDto(user.Id, user.Email, user.DisplayName, user.Role)).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _repo.GetByIdAsync(id, cancellationToken);

        return user is null ? null : new UserDto(user.Id, user.Email, user.DisplayName, user.Role);
    }

    public async Task<Guid> RegisterAsync(RegisterUserRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.", nameof(request));

        var passwordHash = _passwordHasher.Hash(request.Password);
        var user = new User(request.Email, request.DisplayName, passwordHash);

        if (await _repo.EmailExistsAsync(user.Email, cancellationToken))
            throw new ArgumentException($"A user with email '{user.Email}' already exists.", nameof(request));

        await _repo.AddAsync(user, cancellationToken);

        await _publishEndpoint.Publish(
            new UserRegistered(user.Id, user.Email, DateTimeOffset.UtcNow),
            cancellationToken);

        // Single SaveChangesAsync commits both the user row and the buffered
        // outbox message in one DB transaction (MassTransit's UseBusOutbox
        // intercepts SaveChanges to flush the outbox atomically with it).
        // Publish must be called before this, not after: if Publish is called
        // after SaveChangesAsync already committed, the outbox never gets a
        // SaveChanges call to flush into, and the message is silently lost.
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registered user {UserId} with email {Email}", user.Id, user.Email);

        return user.Id;
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var user = await _repo.GetByEmailAsync(request.Email, cancellationToken);

        // Three ways to fail, one answer. An address that does not exist and a locked account
        // each still pay for a verification (against a dummy hash), so neither the message nor the
        // response time says which of the three it was; otherwise lockout, or the timing, would
        // be a way to find out which addresses have accounts.
        if (user is null)
        {
            _passwordHasher.Verify(request.Password, _passwordHasher.DummyHash);
            throw new InvalidCredentialsException();
        }

        if (user.IsLockedAt(now))
        {
            // The real hash is not consulted at all while locked: not even the right password
            // gets in, and a guesser learns nothing from the answer. A refused attempt does not
            // extend the lock.
            _passwordHasher.Verify(request.Password, _passwordHasher.DummyHash);
            _logger.LogWarning("Login refused for locked account {UserId} until {LockedUntil}", user.Id, user.LockedUntilUtc);
            throw new InvalidCredentialsException();
        }

        if (!_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            await _repo.RegisterFailedLoginAsync(user.Id, _lockout, now, cancellationToken);
            throw new InvalidCredentialsException();
        }

        // A write only when there is something to forget, so an ordinary login stays read-only.
        if (user.HasLoginFailures)
            await _repo.ClearLoginFailuresAsync(user.Id, cancellationToken);

        var tokens = await _sessions.StartAsync(user, cancellationToken);

        return new LoginResult(tokens.AccessToken, tokens.AccessExpiresAtUtc, tokens.RefreshToken, tokens.RefreshExpiresAtUtc);
    }
}
