using UserService.Domain;

namespace UserService.Infrastructure;

/// <summary>
/// Deletes refresh tokens that expired more than a day ago, once an hour. Without it every sign-in
/// and every refresh leaves a row behind for ever. A day of grace keeps recently expired tokens
/// around, so presenting one still answers "expired" rather than "never heard of it".
/// </summary>
public class RefreshTokenCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<RefreshTokenCleanupService> _logger;

    public RefreshTokenCleanupService(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RefreshTokenCleanupService> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IRefreshTokenRepository>();
                var deleted = await repository.DeleteExpiredAsync(_clock.GetUtcNow() - Keep, stoppingToken);

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} expired refresh token(s)", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed sweep is retried an hour later; it must never stop the service.
                _logger.LogWarning(ex, "Deleting expired refresh tokens failed; will try again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
