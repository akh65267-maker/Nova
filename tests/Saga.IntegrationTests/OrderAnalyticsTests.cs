using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderService.Application.Analytics;
using OrderService.Domain;
using OrderService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Saga.IntegrationTests;

/// <summary>
/// The admin figures, computed by a real Postgres from orders whose creation times are set
/// by hand, so each expected number is known in advance.
/// </summary>
public class OrderAnalyticsTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Day1 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private DbContextOptions<OrderDbContext> _options = default!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        _options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(_db.GetConnectionString()).Options;
        await using var context = new OrderDbContext(_options);
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    private async Task SeedAsync(DateTimeOffset createdAt, string outcome, params (int Quantity, decimal Price)[] lines)
    {
        var order = new Order(Guid.NewGuid(), lines.Select(l => new OrderItem(Guid.NewGuid(), l.Quantity, l.Price)).ToList());

        if (outcome == "confirmed") order.Confirm();
        else if (outcome == "cancelled") order.Cancel("Out of stock.");

        await using var context = new OrderDbContext(_options);
        context.Orders.Add(order);
        context.Entry(order).Property(nameof(Order.CreatedAtUtc)).CurrentValue = createdAt;
        await context.SaveChangesAsync();
    }

    private async Task<PeriodTotals> TotalsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        await using var context = new OrderDbContext(_options);
        return await new OrderAnalytics(context).GetTotalsAsync(from, to);
    }

    [Fact]
    public async Task Totals_CountEachStatus_AndSumRevenueOverConfirmedOrdersOnly()
    {
        await SeedAsync(Day1.AddHours(1), "confirmed", (2, 10.00m), (1, 5.50m));   // 25.50
        await SeedAsync(Day1.AddHours(2), "confirmed", (3, 4.00m));                 // 12.00
        await SeedAsync(Day1.AddHours(3), "cancelled", (10, 100.00m));              // not revenue
        await SeedAsync(Day1.AddHours(4), "pending", (10, 100.00m));                // not revenue yet

        var totals = await TotalsAsync(Day1, Day1.AddDays(1));

        Assert.Equal(4, totals.Orders);
        Assert.Equal(2, totals.Confirmed);
        Assert.Equal(1, totals.Cancelled);
        Assert.Equal(1, totals.Pending);
        Assert.Equal(37.50m, totals.Revenue);
        Assert.Equal(18.75m, totals.AverageOrderValue);
        Assert.Equal(1m / 3m, totals.CancellationRate);   // 1 cancelled of 3 resolved; pending left out
    }

    [Fact]
    public async Task Totals_IncludeTheStartOfThePeriod_AndExcludeItsEnd()
    {
        await SeedAsync(Day1, "confirmed", (1, 1.00m));                   // exactly at from: in
        await SeedAsync(Day1.AddDays(1), "confirmed", (1, 2.00m));        // exactly at to: out
        await SeedAsync(Day1.AddTicks(-1), "confirmed", (1, 4.00m));      // just before: out

        var totals = await TotalsAsync(Day1, Day1.AddDays(1));

        Assert.Equal(1, totals.Orders);
        Assert.Equal(1.00m, totals.Revenue);
    }

    [Fact]
    public async Task Totals_ForAPeriodWithNoOrders_AreZeroAndSayThereIsNoAverageOrRate()
    {
        await SeedAsync(Day1.AddDays(-5), "confirmed", (1, 9.00m));

        var totals = await TotalsAsync(Day1, Day1.AddDays(1));

        Assert.Equal(new PeriodTotals(0, 0, 0, 0, 0m, null, null), totals);
    }

    [Fact]
    public async Task Totals_WhenNothingHasResolvedYet_HaveNoCancellationRate()
    {
        await SeedAsync(Day1.AddHours(1), "pending", (1, 9.00m));

        var totals = await TotalsAsync(Day1, Day1.AddDays(1));

        Assert.Equal(1, totals.Pending);
        Assert.Null(totals.CancellationRate);
        Assert.Null(totals.AverageOrderValue);
    }

    [Fact]
    public async Task Summary_ComparesThePeriodWithTheEqualOneBeforeIt()
    {
        // Days 1-3 is "this" period (3 days); days -2..0 is the one before.
        await SeedAsync(Day1.AddDays(-2), "confirmed", (1, 10.00m));
        await SeedAsync(Day1.AddDays(-1), "cancelled", (1, 10.00m));
        await SeedAsync(Day1.AddDays(1), "confirmed", (2, 10.00m));
        await SeedAsync(Day1.AddDays(2), "confirmed", (1, 10.00m));
        await SeedAsync(Day1.AddDays(2), "confirmed", (1, 10.00m));

        await using var context = new OrderDbContext(_options);
        var summary = await new OrderAnalyticsService(new OrderAnalytics(context))
            .GetSummaryAsync(Day1, Day1.AddDays(3));

        Assert.Equal(3, summary.Current.Orders);
        Assert.Equal(40.00m, summary.Current.Revenue);
        Assert.Equal(2, summary.Previous.Orders);
        Assert.Equal(10.00m, summary.Previous.Revenue);
        Assert.Equal(0.5m, summary.Previous.CancellationRate);
        Assert.Equal(Day1.AddDays(-3), summary.PreviousFrom);
    }

    [Fact]
    public async Task TheAnalyticsQueryCanUseTheIndexes()
    {
        // With the table too small for the planner to prefer an index, ask it what it would do
        // if scanning were expensive: the indexes the migration created must be usable for the
        // query's shape. A rename or a dropped index shows up here.
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            await off.ExecuteNonQueryAsync();

        await using var explain = new NpgsqlCommand(
            "EXPLAIN SELECT \"Status\", count(*) FROM \"Orders\" " +
            "WHERE \"CreatedAtUtc\" >= now() - interval '7 days' AND \"CreatedAtUtc\" < now() GROUP BY \"Status\"",
            connection);
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync()) plan.Add(reader.GetString(0));

        Assert.Contains(plan, line => line.Contains("IX_Orders_") && line.Contains("CreatedAtUtc"));
    }

    private async Task<Guid> SeedOrderAsync(
        DateTimeOffset createdAt, string outcome, Guid? userId = null, string? reason = "Out of stock.",
        params (Guid ProductId, int Quantity, decimal Price)[] lines)
    {
        var items = lines.Length == 0
            ? new List<OrderItem> { new(Guid.NewGuid(), 1, 1.00m) }
            : lines.Select(l => new OrderItem(l.ProductId, l.Quantity, l.Price)).ToList();
        var order = new Order(userId ?? Guid.NewGuid(), items);

        if (outcome == "confirmed") order.Confirm();
        else if (outcome == "cancelled") order.Cancel(reason!);

        await using var context = new OrderDbContext(_options);
        context.Orders.Add(order);
        context.Entry(order).Property(nameof(Order.CreatedAtUtc)).CurrentValue = createdAt;
        await context.SaveChangesAsync();
        return order.Id;
    }

    private async Task<T> WithAnalyticsAsync<T>(Func<OrderAnalytics, Task<T>> action)
    {
        await using var context = new OrderDbContext(_options);
        return await action(new OrderAnalytics(context));
    }

    [Fact]
    public async Task Daily_GroupsByUtcDay_AndAMinuteEitherSideOfMidnightLandsOnTheRightDay()
    {
        await SeedAsync(Day1.AddHours(10), "confirmed", (2, 5.00m));                 // day 1: 10.00
        await SeedAsync(Day1.AddHours(11), "confirmed", (1, 3.00m));                 // day 1: 3.00
        await SeedAsync(Day1.AddHours(12), "cancelled", (9, 9.00m));                 // day 1: not revenue
        await SeedAsync(Day1.AddDays(1).AddMinutes(-1), "pending", (1, 1.00m));      // 23:59 day 1
        await SeedAsync(Day1.AddDays(1), "confirmed", (1, 7.00m));                   // 00:00 day 2
        // Day 3 has nothing.

        var days = await WithAnalyticsAsync(a => a.GetDailyAsync(Day1, Day1.AddDays(3)));

        Assert.Equal(2, days.Count);
        Assert.Equal(new DailyPoint(new DateOnly(2026, 10, 1), 4, 1, 2, 1, 13.00m), days[0]);
        Assert.Equal(new DailyPoint(new DateOnly(2026, 10, 2), 1, 0, 1, 0, 7.00m), days[1]);
    }

    [Fact]
    public async Task Cancellations_AreGroupedByReason_MostCommonFirst_AndOnlyForCancelledOrdersInRange()
    {
        await SeedOrderAsync(Day1.AddHours(1), "cancelled", reason: "Out of stock.");
        await SeedOrderAsync(Day1.AddHours(2), "cancelled", reason: "Out of stock.");
        await SeedOrderAsync(Day1.AddHours(3), "cancelled", reason: "Checkout timed out.");
        await SeedOrderAsync(Day1.AddHours(4), "cancelled", reason: "");
        await SeedOrderAsync(Day1.AddHours(5), "confirmed");                          // not cancelled
        await SeedOrderAsync(Day1.AddDays(-1), "cancelled", reason: "Out of stock."); // out of range

        var reasons = await WithAnalyticsAsync(a => a.GetCancellationsAsync(Day1, Day1.AddDays(1)));

        Assert.Equal(
            new[]
            {
                new CancellationReasonCount("Out of stock.", 2),
                new CancellationReasonCount("Checkout timed out.", 1),
                new CancellationReasonCount("Not recorded", 1),
            },
            reasons);
    }

    [Fact]
    public async Task TopProducts_RankByUnitsOrByRevenue_CountingConfirmedOrdersOnly()
    {
        var cheapAndPopular = Guid.NewGuid();
        var dearAndRare = Guid.NewGuid();
        var neverSold = Guid.NewGuid();

        await SeedOrderAsync(Day1.AddHours(1), "confirmed", lines: new[] { (cheapAndPopular, 10, 1.00m), (dearAndRare, 1, 20.00m) });
        await SeedOrderAsync(Day1.AddHours(2), "confirmed", lines: new[] { (dearAndRare, 1, 20.00m) });
        await SeedOrderAsync(Day1.AddHours(3), "cancelled", lines: new[] { (neverSold, 500, 99.00m) });
        await SeedOrderAsync(Day1.AddHours(4), "pending", lines: new[] { (neverSold, 500, 99.00m) });

        var byUnits = await WithAnalyticsAsync(a => a.GetTopProductsAsync(Day1, Day1.AddDays(1), TopProductsBy.Units, 10));
        var byRevenue = await WithAnalyticsAsync(a => a.GetTopProductsAsync(Day1, Day1.AddDays(1), TopProductsBy.Revenue, 10));
        var topOne = await WithAnalyticsAsync(a => a.GetTopProductsAsync(Day1, Day1.AddDays(1), TopProductsBy.Revenue, 1));

        Assert.Equal(new[] { cheapAndPopular, dearAndRare }, byUnits.Select(p => p.ProductId));
        Assert.Equal(new TopProduct(cheapAndPopular, 10, 10.00m), byUnits[0]);
        Assert.Equal(new TopProduct(dearAndRare, 2, 40.00m), byUnits[1]);
        Assert.Equal(new[] { dearAndRare, cheapAndPopular }, byRevenue.Select(p => p.ProductId));
        Assert.Single(topOne);
        Assert.DoesNotContain(byUnits, p => p.ProductId == neverSold);
    }

    [Fact]
    public async Task Pending_ListsOnlyOldPendingOrders_OldestFirst_UpToTheLimit()
    {
        var now = Day1.AddHours(12);
        var oldest = await SeedOrderAsync(now.AddMinutes(-30), "pending");
        var older = await SeedOrderAsync(now.AddMinutes(-10), "pending");
        await SeedOrderAsync(now.AddSeconds(-30), "pending");          // too recent to be "stuck"
        await SeedOrderAsync(now.AddMinutes(-40), "confirmed");        // old, but resolved
        await SeedOrderAsync(now.AddMinutes(-50), "cancelled");

        var stuck = await WithAnalyticsAsync(a => a.GetPendingAsync(now.AddSeconds(-120), now, 50));
        var limited = await WithAnalyticsAsync(a => a.GetPendingAsync(now.AddSeconds(-120), now, 1));

        Assert.Equal(new[] { oldest, older }, stuck.Select(p => p.Id));
        Assert.Equal(30 * 60, stuck[0].AgeSeconds);
        Assert.Equal(oldest, Assert.Single(limited).Id);
    }

    [Fact]
    public async Task Orders_ArePagedNewestFirst_FilteredByStatus_WithTotalsAndLineCounts()
    {
        var user = Guid.NewGuid();
        var first = await SeedOrderAsync(Day1.AddHours(1), "confirmed", user, lines: new[] { (Guid.NewGuid(), 2, 5.00m), (Guid.NewGuid(), 1, 2.50m) });
        var second = await SeedOrderAsync(Day1.AddHours(2), "cancelled", user, reason: "Out of stock.");
        var third = await SeedOrderAsync(Day1.AddHours(3), "confirmed", user);
        await SeedOrderAsync(Day1.AddDays(5), "confirmed");   // out of range

        var all = await WithAnalyticsAsync(a => a.GetOrdersAsync(null, Day1, Day1.AddDays(1), 1, 2));
        var rest = await WithAnalyticsAsync(a => a.GetOrdersAsync(null, Day1, Day1.AddDays(1), 2, 2));
        var confirmed = await WithAnalyticsAsync(a => a.GetOrdersAsync(OrderStatus.Confirmed, Day1, Day1.AddDays(1), 1, 25));
        var beyond = await WithAnalyticsAsync(a => a.GetOrdersAsync(null, Day1, Day1.AddDays(1), 9, 2));

        Assert.Equal(3, all.Total);
        Assert.Equal(new[] { third, second }, all.Items.Select(o => o.Id));
        Assert.Equal(new[] { first }, rest.Items.Select(o => o.Id));
        Assert.Equal(2, confirmed.Total);
        Assert.All(confirmed.Items, o => Assert.Equal(OrderStatus.Confirmed, o.Status));
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.Total);

        var firstRow = Assert.Single(rest.Items);
        Assert.Equal(12.50m, firstRow.Total);
        Assert.Equal(2, firstRow.LineCount);
        Assert.Equal(user, firstRow.UserId);
        Assert.NotNull(firstRow.ResolvedAtUtc);

        var cancelledRow = all.Items[1];
        Assert.Equal("Out of stock.", cancelledRow.CancellationReason);
    }
}
