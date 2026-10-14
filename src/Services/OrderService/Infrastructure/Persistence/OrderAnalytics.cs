using Microsoft.EntityFrameworkCore;
using OrderService.Application.Analytics;
using OrderService.Domain;

namespace OrderService.Infrastructure.Persistence;

public class OrderAnalytics : IOrderAnalytics
{
    private const string NoReason = "Not recorded";

    private readonly OrderDbContext _db;

    public OrderAnalytics(OrderDbContext db)
    {
        _db = db;
    }

    // Shapes of the raw SQL queries below; the property names are the column aliases.
    private sealed class DailyRow
    {
        public DateOnly Day { get; set; }
        public string Status { get; set; } = "";
        public int Count { get; set; }
        public decimal Value { get; set; }
    }

    private sealed class TopProductRow
    {
        public Guid ProductId { get; set; }
        public int Units { get; set; }
        public decimal Revenue { get; set; }
    }

    private IQueryable<Order> CreatedBetween(DateTimeOffset from, DateTimeOffset to) =>
        _db.Orders.AsNoTracking().Where(o => o.CreatedAtUtc >= from && o.CreatedAtUtc < to);

    public async Task<PeriodTotals> GetTotalsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        // One grouped query, served by the (Status, CreatedAtUtc) / (CreatedAtUtc) indexes.
        var rows = await CreatedBetween(from, to)
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

    public async Task<IReadOnlyList<DailyPoint>> GetDailyAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        // Grouped by UTC calendar day and status in SQL, because the provider cannot translate
        // "the UTC date of a DateTimeOffset" and a session time zone must not decide the day.
        // The days are folded together in memory (at most 366 days x 3 statuses).
        var rows = await _db.Database.SqlQuery<DailyRow>($"""
            SELECT (o."CreatedAtUtc" AT TIME ZONE 'UTC')::date AS "Day",
                   o."Status" AS "Status",
                   count(*)::int AS "Count",
                   COALESCE(SUM(t."Total"), 0) AS "Value"
            FROM "Orders" o
            LEFT JOIN LATERAL (
                SELECT SUM(i."Quantity" * i."UnitPrice") AS "Total"
                FROM "OrderItem" i WHERE i."OrderId" = o."Id") t ON true
            WHERE o."CreatedAtUtc" >= {from} AND o."CreatedAtUtc" < {to}
            GROUP BY 1, 2
            """).ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.Day)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                int Count(OrderStatus s) => g.Where(r => r.Status == s.ToString()).Sum(r => r.Count);

                var pending = Count(OrderStatus.Pending);
                var confirmed = Count(OrderStatus.Confirmed);
                var cancelled = Count(OrderStatus.Cancelled);
                var revenue = g.Where(r => r.Status == nameof(OrderStatus.Confirmed)).Sum(r => r.Value);

                return new DailyPoint(g.Key, pending + confirmed + cancelled, pending, confirmed, cancelled, revenue);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<CancellationReasonCount>> GetCancellationsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var rows = await CreatedBetween(from, to)
            .Where(o => o.Status == OrderStatus.Cancelled)
            .GroupBy(o => o.CancellationReason)
            .Select(g => new { Reason = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // A missing and an empty reason are the same thing to the reader.
        return rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Reason) ? NoReason : r.Reason!)
            .Select(g => new CancellationReasonCount(g.Key, g.Sum(r => r.Count)))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(
        DateTimeOffset from, DateTimeOffset to, TopProductsBy by, int limit, CancellationToken cancellationToken = default)
    {
        // Plain SQL: the lines are an owned collection, which cannot be queried and grouped on
        // its own. The product id breaks ties so the order is the same every time. The two
        // queries differ only in the ORDER BY, which cannot be a parameter.
        var query = by == TopProductsBy.Revenue
            ? _db.Database.SqlQuery<TopProductRow>($"""
                SELECT i."ProductId" AS "ProductId",
                       SUM(i."Quantity")::int AS "Units",
                       SUM(i."Quantity" * i."UnitPrice") AS "Revenue"
                FROM "OrderItem" i JOIN "Orders" o ON o."Id" = i."OrderId"
                WHERE o."Status" = 'Confirmed' AND o."CreatedAtUtc" >= {from} AND o."CreatedAtUtc" < {to}
                GROUP BY i."ProductId"
                ORDER BY SUM(i."Quantity" * i."UnitPrice") DESC, i."ProductId"
                LIMIT {limit}
                """)
            : _db.Database.SqlQuery<TopProductRow>($"""
                SELECT i."ProductId" AS "ProductId",
                       SUM(i."Quantity")::int AS "Units",
                       SUM(i."Quantity" * i."UnitPrice") AS "Revenue"
                FROM "OrderItem" i JOIN "Orders" o ON o."Id" = i."OrderId"
                WHERE o."Status" = 'Confirmed' AND o."CreatedAtUtc" >= {from} AND o."CreatedAtUtc" < {to}
                GROUP BY i."ProductId"
                ORDER BY SUM(i."Quantity") DESC, i."ProductId"
                LIMIT {limit}
                """);

        var rows = await query.ToListAsync(cancellationToken);
        return rows.Select(r => new TopProduct(r.ProductId, r.Units, r.Revenue)).ToList();
    }

    public async Task<IReadOnlyList<PendingOrder>> GetPendingAsync(
        DateTimeOffset createdBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await _db.Orders
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pending && o.CreatedAtUtc < createdBefore)
            .OrderBy(o => o.CreatedAtUtc)
            .ThenBy(o => o.Id)
            .Take(limit)
            .Select(o => new
            {
                o.Id,
                o.UserId,
                o.CreatedAtUtc,
                Total = o.Items.Sum(i => i.Quantity * i.UnitPrice),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new PendingOrder(r.Id, r.UserId, r.CreatedAtUtc, r.Total, (int)(now - r.CreatedAtUtc).TotalSeconds))
            .ToList();
    }

    public async Task<PagedResult<AdminOrderRow>> GetOrdersAsync(
        OrderStatus? status, DateTimeOffset from, DateTimeOffset to, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = CreatedBetween(from, to);
        if (status is not null)
            query = query.Where(o => o.Status == status);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(o => o.CreatedAtUtc)
            .ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new AdminOrderRow(
                o.Id,
                o.UserId,
                o.Status,
                o.CreatedAtUtc,
                o.ResolvedAtUtc,
                o.Items.Sum(i => i.Quantity * i.UnitPrice),
                o.Items.Count,
                o.CancellationReason))
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminOrderRow>(items, page, pageSize, total);
    }
}
