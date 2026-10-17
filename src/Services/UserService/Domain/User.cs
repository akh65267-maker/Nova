namespace UserService.Domain;

public class User
{
    public Guid Id { get; private set; }
    public string Email { get; private set; }
    public string DisplayName { get; private set; }
    public string PasswordHash { get; private set; }
    public UserRole Role { get; private set; }

    // Wrong-password tracking, changed only by the repository's atomic statements (see
    // IUserRepository.RegisterFailedLoginAsync), never by read-modify-write here: parallel
    // guesses must not be able to slip past the counter.
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LastFailedLoginAtUtc { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }

    public bool IsLockedAt(DateTimeOffset now) => LockedUntilUtc is not null && LockedUntilUtc > now;

    public bool HasLoginFailures => FailedLoginCount > 0 || LastFailedLoginAtUtc is not null || LockedUntilUtc is not null;

    private User()
    {
    }

    public User(string email, string displayName, string passwordHash, UserRole role = UserRole.Customer)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));

        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));

        Id = Guid.NewGuid();
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        Role = role;
    }
}
