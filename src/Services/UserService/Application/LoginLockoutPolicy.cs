namespace UserService.Application;

/// <summary>
/// When repeated wrong passwords lock an account. <paramref name="MaxFailures"/> failures within
/// <paramref name="Window"/> of each other lock it for <paramref name="Duration"/>. The lock is
/// temporary on purpose: a permanent one would let anybody lock anybody else out for good.
/// Bound from the "Login:Lockout" configuration section.
/// </summary>
public sealed record LoginLockoutPolicy(int MaxFailures, TimeSpan Window, TimeSpan Duration)
{
    /// <summary>
    /// Refuses a policy that would misbehave: a half-filled configuration section binds the missing
    /// values as zero, and zero failures to lock would lock every account on its first wrong password.
    /// </summary>
    public LoginLockoutPolicy Validated()
    {
        if (MaxFailures < 1)
            throw new InvalidOperationException("Login:Lockout:MaxFailures must be at least 1.");
        if (Window <= TimeSpan.Zero || Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("Login:Lockout:Window and Duration must be greater than zero.");
        return this;
    }

    public static LoginLockoutPolicy Default { get; } =
        new(MaxFailures: 5, Window: TimeSpan.FromMinutes(15), Duration: TimeSpan.FromMinutes(15));
}
