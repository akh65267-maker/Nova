namespace OrderService.Application.Analytics;

public interface IOrderAnalytics
{
    /// <summary>Totals for orders created in [from, to).</summary>
    Task<PeriodTotals> GetTotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}
