namespace UserService.Domain;

/// <summary>
/// One refresh token, as stored. The token itself is never stored, only its SHA-256 hash, so a
/// copy of the table cannot be used to sign anyone in. Tokens form a "family": each refresh marks
/// the token it was given as used and issues the next one in the same family, so a family is one
/// sign-in's chain of tokens, and revoking the family ends that sign-in.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; }

    /// <summary>When the sign-in that started this family happened; the family has an absolute lifetime from here.</summary>
    public DateTimeOffset FamilyCreatedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Set when this token was exchanged for the next one. A used token must never be presented again.</summary>
    public DateTimeOffset? UsedAtUtc { get; private set; }

    /// <summary>Set on every token of a family when the family is ended (sign-out, or a token was reused).</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    private RefreshToken()
    {
    }

    public RefreshToken(
        Guid userId, Guid familyId, string tokenHash, DateTimeOffset familyCreatedAtUtc, DateTimeOffset createdAtUtc, DateTimeOffset expiresAtUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new ArgumentException("Token hash is required.", nameof(tokenHash));

        Id = Guid.NewGuid();
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        FamilyCreatedAtUtc = familyCreatedAtUtc;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }
}
