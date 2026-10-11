using OrderService.Application.Analytics;

namespace OrderService.UnitTests.Application;

public class OrderAnalyticsServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeAnalytics : IOrderAnalytics
    {
        public List<(DateTimeOffset From, DateTimeOffset To)> Calls { get; } = new();

        public Task<PeriodTotals> GetTotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to));
            return Task.FromResult(new PeriodTotals(Calls.Count, 0, 0, 0, 0, null, null));
        }
    }

    [Fact]
    public async Task Summary_AsksForThePeriodAndForTheEqualPeriodRightBeforeIt()
    {
        var fake = new FakeAnalytics();

        var summary = await new OrderAnalyticsService(fake).GetSummaryAsync(Start, Start.AddDays(7));

        Assert.Equal(new[] { (Start, Start.AddDays(7)), (Start.AddDays(-7), Start) }, fake.Calls);
        Assert.Equal(Start.AddDays(-7), summary.PreviousFrom);
        Assert.Equal(Start, summary.PreviousTo);
        Assert.Equal(1, summary.Current.Orders);
        Assert.Equal(2, summary.Previous.Orders);
    }

    [Fact]
    public async Task Summary_ReadsOffsetsAsUtc()
    {
        var fake = new FakeAnalytics();
        var plusTwo = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var summary = await new OrderAnalyticsService(fake).GetSummaryAsync(plusTwo, plusTwo.AddDays(1));

        Assert.Equal(Start, summary.From);
        Assert.Equal(TimeSpan.Zero, summary.From.Offset);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Summary_RequiresBothEnds(bool hasFrom, bool hasTo)
    {
        DateTimeOffset? from = hasFrom ? Start : null;
        DateTimeOffset? to = hasTo ? Start.AddDays(1) : null;

        await Assert.ThrowsAsync<ArgumentException>(
            () => new OrderAnalyticsService(new FakeAnalytics()).GetSummaryAsync(from, to));
    }

    [Fact]
    public async Task Summary_RefusesAReversedOrEmptyRange()
    {
        var service = new OrderAnalyticsService(new FakeAnalytics());

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSummaryAsync(Start, Start.AddDays(-1)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSummaryAsync(Start, Start));
    }

    [Fact]
    public async Task Summary_RefusesARangeLongerThanAYear_ButAllowsExactlyTheMaximum()
    {
        var fake = new FakeAnalytics();
        var service = new OrderAnalyticsService(fake);

        await service.GetSummaryAsync(Start, Start + OrderAnalyticsService.MaxRange);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetSummaryAsync(Start, Start + OrderAnalyticsService.MaxRange + TimeSpan.FromSeconds(1)));
    }
}
