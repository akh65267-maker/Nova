using Microsoft.EntityFrameworkCore;
using OrderService.Application.Analytics;
using OrderService.Domain;

namespace OrderService.Infrastructure.Persistence;

public class OrderAnalytics : IOrderAnalytics
{
    private readonly OrderDbContext _db;

    public OrderAnalytics(OrderDbContext db)
    {
        _db = db;
    }

    public async Task<PeriodTotals> GetTotalsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        // One grouped query, served by the (Status, CreatedAtUtc) / (CreatedAtUtc) indexes.
        var rows = await _db.Orders
            .AsNoTracking()
            .Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to)
            .GroupBy(o => o.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                Value = g.Sum(o => o.Items.Sum(i => i.Quantity * i.UnitPrice)),
            })
            .ToListAsync(cancellationToken);

        int Count(OrderStatus s) => rows.Where(r => r.Status == s).Sum(r => r.Count);

        var pending = Count(OrderStatus.Pending);
        var confirmed = Count(OrderStatus.Confirmed);
        var cancelled = Count(OrderStatus.Cancelled);
        var revenue = rows.Where(r => r.Status == OrderStatus.Confirmed).Sum(r => r.Value);
        var resolved = confirmed + cancelled;

        return new PeriodTotals(
            pending + confirmed + cancelled,
            pending,
            confirmed,
            cancelled,
            revenue,
            confirmed == 0 ? null : revenue / confirmed,
            resolved == 0 ? null : (decimal)cancelled / resolved);
    }
}
