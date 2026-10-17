namespace UserService.Application;

/// <summary>
/// How long a sign-in lasts. Bound from the "RefreshTokens" configuration section.
/// </summary>
/// <param name="RefreshSlidingDays">Each refresh token is good for this long from when it was issued.</param>
/// <param name="RefreshAbsoluteDays">A sign-in ends this long after it started, however often it was refreshed.</param>
/// <param name="ReuseGraceSeconds">
/// A token used again within this many seconds of its first use is refused but does not end the
/// sign-in: two tabs refreshing at the same moment is an ordinary race, not a stolen token.
/// Used again later, it does end the sign-in.
/// </param>
public sealed record RefreshTokenOptions(int RefreshSlidingDays, int RefreshAbsoluteDays, int ReuseGraceSeconds)
{
    public static RefreshTokenOptions Default { get; } = new(RefreshSlidingDays: 14, RefreshAbsoluteDays: 30, ReuseGraceSeconds: 10);

    public RefreshTokenOptions Validated()
    {
        if (RefreshSlidingDays < 1 || RefreshAbsoluteDays < RefreshSlidingDays || ReuseGraceSeconds < 0)
            throw new InvalidOperationException(
                "RefreshTokens: RefreshSlidingDays must be at least 1, RefreshAbsoluteDays at least RefreshSlidingDays, ReuseGraceSeconds not negative.");
        return this;
    }
}
