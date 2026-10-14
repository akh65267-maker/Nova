using OrderService.Domain;

namespace OrderService.Application.Analytics;

/// <summary>One UTC calendar day. Revenue is over the day's confirmed orders.</summary>
public sealed record DailyPoint(DateOnly Day, int Orders, int Pending, int Confirmed, int Cancelled, decimal Revenue);

public sealed record Timeseries(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<DailyPoint> Days);

/// <summary>Cancelled orders sharing a reason. Orders cancelled with no reason recorded are grouped as "Not recorded".</summary>
public sealed record CancellationReasonCount(string Reason, int Count);

/// <summary>Units and revenue over confirmed orders. Names live in CatalogService, not here.</summary>
public sealed record TopProduct(Guid ProductId, int Units, decimal Revenue);

public sealed record PendingOrder(Guid Id, Guid UserId, DateTimeOffset CreatedAtUtc, decimal Total, int AgeSeconds);

public sealed record AdminOrderRow(
    Guid Id,
    Guid UserId,
    OrderStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    decimal Total,
    int LineCount,
    string? CancellationReason);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public enum TopProductsBy
{
    Units,
    Revenue,
}
