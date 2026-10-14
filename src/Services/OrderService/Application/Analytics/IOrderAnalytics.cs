using OrderService.Domain;

namespace OrderService.Application.Analytics;

public interface IOrderAnalytics
{
    /// <summary>Totals for orders created in [from, to).</summary>
    Task<PeriodTotals> GetTotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>The days that have orders, for orders created in [from, to). Days with none are absent.</summary>
    Task<IReadOnlyList<DailyPoint>> GetDailyAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CancellationReasonCount>> GetCancellationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(DateTimeOffset from, DateTimeOffset to, TopProductsBy by, int limit, CancellationToken cancellationToken = default);

    /// <summary>Pending orders created before <paramref name="createdBefore"/>, oldest first.</summary>
    Task<IReadOnlyList<PendingOrder>> GetPendingAsync(DateTimeOffset createdBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    Task<PagedResult<AdminOrderRow>> GetOrdersAsync(OrderStatus? status, DateTimeOffset from, DateTimeOffset to, int page, int pageSize, CancellationToken cancellationToken = default);
}
