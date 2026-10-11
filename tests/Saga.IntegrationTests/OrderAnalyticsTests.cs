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
}
