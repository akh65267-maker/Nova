namespace OrderService.Application.Analytics;

public class OrderAnalyticsService
{
    // A bound on how much of the order table one request may scan.
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    private readonly IOrderAnalytics _analytics;

    public OrderAnalyticsService(IOrderAnalytics analytics)
    {
        _analytics = analytics;
    }

    public async Task<OrderSummary> GetSummaryAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);

        // The previous period is as long as this one and ends where it starts, so the two
        // can be compared like for like.
        var previousStart = start - (end - start);

        var current = await _analytics.GetTotalsAsync(start, end, cancellationToken);
        var previous = await _analytics.GetTotalsAsync(previousStart, start, cancellationToken);

        return new OrderSummary(start, end, current, previousStart, start, previous);
    }

    // Throws ArgumentException, which the API answers with 400.
    public static (DateTimeOffset From, DateTimeOffset To) ValidateRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null)
            throw new ArgumentException("Both 'from' and 'to' are required.");

        var start = from.Value.ToUniversalTime();
        var end = to.Value.ToUniversalTime();

        if (end <= start)
            throw new ArgumentException("'to' must be after 'from'.");

        if (end - start > MaxRange)
            throw new ArgumentException($"The range must not be longer than {MaxRange.TotalDays:0} days.");

        return (start, end);
    }
}
