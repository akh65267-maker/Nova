using OrderService.Domain;

namespace OrderService.Application.Analytics;

public class OrderAnalyticsService
{
    // A bound on how much of the order table one request may scan.
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(366);

    public const int MaxTopProducts = 50;
    public const int DefaultTopProducts = 10;
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 25;
    public const int MaxPending = 100;
    public const int DefaultPending = 50;
    public const int DefaultPendingMinAgeSeconds = 120;

    private readonly IOrderAnalytics _analytics;
    private readonly TimeProvider _clock;

    public OrderAnalyticsService(IOrderAnalytics analytics, TimeProvider? clock = null)
    {
        _analytics = analytics;
        _clock = clock ?? TimeProvider.System;
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

    /// <summary>One point per UTC day in the range, including days with no orders (as zeros).</summary>
    public async Task<Timeseries> GetTimeseriesAsync(
        DateTimeOffset? from, DateTimeOffset? to, string? bucket, CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);

        if (bucket is not null && !string.Equals(bucket, "day", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only bucket=day is supported.");

        var found = (await _analytics.GetDailyAsync(start, end, cancellationToken)).ToDictionary(p => p.Day);

        var days = new List<DailyPoint>();
        var lastDay = DateOnly.FromDateTime(end.AddTicks(-1).UtcDateTime);
        for (var day = DateOnly.FromDateTime(start.UtcDateTime); day <= lastDay; day = day.AddDays(1))
            days.Add(found.TryGetValue(day, out var point) ? point : new DailyPoint(day, 0, 0, 0, 0, 0m));

        return new Timeseries(start, end, days);
    }

    public async Task<IReadOnlyList<CancellationReasonCount>> GetCancellationsAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);
        return await _analytics.GetCancellationsAsync(start, end, cancellationToken);
    }

    public async Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(
        DateTimeOffset? from, DateTimeOffset? to, string? by, int? limit, CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);

        var order = by?.ToLowerInvariant() switch
        {
            null or "units" => TopProductsBy.Units,
            "revenue" => TopProductsBy.Revenue,
            _ => throw new ArgumentException("'by' must be 'units' or 'revenue'."),
        };

        var count = limit ?? DefaultTopProducts;
        if (count < 1 || count > MaxTopProducts)
            throw new ArgumentException($"'limit' must be between 1 and {MaxTopProducts}.");

        return await _analytics.GetTopProductsAsync(start, end, order, count, cancellationToken);
    }

    /// <summary>Pending orders older than <paramref name="minAgeSeconds"/>, oldest first: the "stuck" list.</summary>
    public async Task<IReadOnlyList<PendingOrder>> GetPendingAsync(
        int? minAgeSeconds, int? limit, CancellationToken cancellationToken = default)
    {
        var age = minAgeSeconds ?? DefaultPendingMinAgeSeconds;
        if (age < 0)
            throw new ArgumentException("'minAgeSeconds' must not be negative.");

        var count = limit ?? DefaultPending;
        if (count < 1 || count > MaxPending)
            throw new ArgumentException($"'limit' must be between 1 and {MaxPending}.");

        var now = _clock.GetUtcNow();
        return await _analytics.GetPendingAsync(now.AddSeconds(-age), now, count, cancellationToken);
    }

    public async Task<PagedResult<AdminOrderRow>> GetOrdersAsync(
        string? status, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var (start, end) = ValidateRange(from, to);

        OrderStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                throw new ArgumentException("'status' must be Pending, Confirmed or Cancelled.");
            parsed = value;
        }

        var pageNumber = page ?? 1;
        if (pageNumber < 1)
            throw new ArgumentException("'page' must be 1 or more.");

        var size = pageSize ?? DefaultPageSize;
        if (size < 1 || size > MaxPageSize)
            throw new ArgumentException($"'pageSize' must be between 1 and {MaxPageSize}.");

        return await _analytics.GetOrdersAsync(parsed, start, end, pageNumber, size, cancellationToken);
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
