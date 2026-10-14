namespace OrderService.Application.Analytics;

/// <summary>
/// Figures for one period. Revenue is the sum of quantity x unit price over the period's
/// <b>confirmed</b> orders, at the price charged: no tax, shipping, discounts or refunds exist
/// in the system. Orders are placed in a period by the time they were created.
/// </summary>
/// <param name="AverageOrderValue">Revenue per confirmed order; null when there are none.</param>
/// <param name="CancellationRate">Cancelled / (confirmed + cancelled), 0 to 1; null while nothing has resolved. Pending orders are left out: they have no outcome yet.</param>
public sealed record PeriodTotals(
    int Orders,
    int Pending,
    int Confirmed,
    int Cancelled,
    decimal Revenue,
    decimal? AverageOrderValue,
    decimal? CancellationRate);

/// <summary>The period asked for ([From, To), UTC) and the one of equal length just before it.</summary>
public sealed record OrderSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    PeriodTotals Current,
    DateTimeOffset PreviousFrom,
    DateTimeOffset PreviousTo,
    PeriodTotals Previous);
