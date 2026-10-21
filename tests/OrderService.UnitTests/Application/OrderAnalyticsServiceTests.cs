using OrderService.Application.Analytics;
using OrderService.Domain;

namespace OrderService.UnitTests.Application;

public class OrderAnalyticsServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeAnalytics : IOrderAnalytics
    {
        public List<(DateTimeOffset From, DateTimeOffset To)> Calls { get; } = new();
        public List<DailyPoint> Daily { get; } = new();
        public (DateTimeOffset CreatedBefore, DateTimeOffset Now, int Limit)? PendingCall { get; private set; }
        public (TopProductsBy By, int Limit)? TopCall { get; private set; }
        public List<TopProduct> TopProducts { get; } = new();
        public (OrderStatus? Status, int Page, int PageSize)? OrdersCall { get; private set; }

        public Task<PeriodTotals> GetTotalsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to));
            return Task.FromResult(new PeriodTotals(Calls.Count, 0, 0, 0, 0, null, null));
        }

        public Task<IReadOnlyList<DailyPoint>> GetDailyAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to));
            return Task.FromResult<IReadOnlyList<DailyPoint>>(Daily);
        }

        public Task<IReadOnlyList<CancellationReasonCount>> GetCancellationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to));
            return Task.FromResult<IReadOnlyList<CancellationReasonCount>>(Array.Empty<CancellationReasonCount>());
        }

        public Task<IReadOnlyList<TopProduct>> GetTopProductsAsync(DateTimeOffset from, DateTimeOffset to, TopProductsBy by, int limit, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to));
            TopCall = (by, limit);
            return Task.FromResult<IReadOnlyList<TopProduct>>(TopProducts);
        }

        public Task<IReadOnlyList<PendingOrder>> GetPendingAsync(DateTimeOffset createdBefore, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
        {
            PendingCall = (createdBefore, now, limit);
            return Task.FromResult<IReadOnlyList<PendingOrder>>(Array.Empty<PendingOrder>());
        }

        public Task<PagedResult<AdminOrderRow>> GetOrdersAsync(OrderStatus? status, DateTimeOffset from, DateTimeOffset to, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to));
            OrdersCall = (status, page, pageSize);
            return Task.FromResult(new PagedResult<AdminOrderRow>(Array.Empty<AdminOrderRow>(), page, pageSize, 0));
        }
    }

    private static OrderAnalyticsService Service(FakeAnalytics fake, TimeProvider? clock = null) => new(fake, clock);

    [Fact]
    public async Task Summary_AsksForThePeriodAndForTheEqualPeriodRightBeforeIt()
    {
        var fake = new FakeAnalytics();

        var summary = await Service(fake).GetSummaryAsync(Start, Start.AddDays(7));

        Assert.Equal(new[] { (Start, Start.AddDays(7)), (Start.AddDays(-7), Start) }, fake.Calls);
        Assert.Equal(Start.AddDays(-7), summary.PreviousFrom);
        Assert.Equal(Start, summary.PreviousTo);
        Assert.Equal(1, summary.Current.Orders);
        Assert.Equal(2, summary.Previous.Orders);
    }

    [Fact]
    public async Task Summary_ReadsOffsetsAsUtc()
    {
        var plusTwo = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2));

        var summary = await Service(new FakeAnalytics()).GetSummaryAsync(plusTwo, plusTwo.AddDays(1));

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

        await Assert.ThrowsAsync<ArgumentException>(() => Service(new FakeAnalytics()).GetSummaryAsync(from, to));
    }

    [Fact]
    public async Task Summary_RefusesAReversedOrEmptyRange()
    {
        var service = Service(new FakeAnalytics());

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSummaryAsync(Start, Start.AddDays(-1)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSummaryAsync(Start, Start));
    }

    [Fact]
    public async Task Summary_RefusesARangeLongerThanAYear_ButAllowsExactlyTheMaximum()
    {
        var service = Service(new FakeAnalytics());

        await service.GetSummaryAsync(Start, Start + OrderAnalyticsService.MaxRange);
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetSummaryAsync(Start, Start + OrderAnalyticsService.MaxRange + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Timeseries_HasOnePointPerUtcDay_FillingDaysWithNoOrdersWithZeros()
    {
        var fake = new FakeAnalytics();
        fake.Daily.Add(new DailyPoint(new DateOnly(2026, 10, 2), 3, 1, 1, 1, 20m));

        var series = await Service(fake).GetTimeseriesAsync(Start, Start.AddDays(4), "day");

        Assert.Equal(
            new[] { 1, 2, 3, 4 },
            series.Days.Select(d => d.Day.Day));
        Assert.Equal(new[] { 0, 3, 0, 0 }, series.Days.Select(d => d.Orders));
        Assert.Equal(20m, series.Days[1].Revenue);
        Assert.Equal(0m, series.Days[0].Revenue);
    }

    [Fact]
    public async Task Timeseries_ForARangeThatStartsMidDay_IncludesBothPartialDays()
    {
        var series = await Service(new FakeAnalytics())
            .GetTimeseriesAsync(Start.AddHours(18), Start.AddDays(1).AddHours(6), null);

        Assert.Equal(new[] { 1, 2 }, series.Days.Select(d => d.Day.Day));
    }

    [Fact]
    public async Task Timeseries_RefusesABucketItDoesNotSupport()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(new FakeAnalytics()).GetTimeseriesAsync(Start, Start.AddDays(1), "hour"));
    }

    [Fact]
    public async Task TopProducts_DefaultsToTenByUnits_AndReadsTheOrdering()
    {
        var fake = new FakeAnalytics();

        await Service(fake).GetTopProductsAsync(Start, Start.AddDays(1), null, null);
        Assert.Equal((TopProductsBy.Units, 10), fake.TopCall);

        await Service(fake).GetTopProductsAsync(Start, Start.AddDays(1), "Revenue", 5);
        Assert.Equal((TopProductsBy.Revenue, 5), fake.TopCall);
    }

    [Theory]
    [InlineData("profit", 10)]
    [InlineData("units", 0)]
    [InlineData("units", 51)]
    [InlineData("units", -1)]
    public async Task TopProducts_RefusesAnUnknownOrderingAndAnOutOfBoundsLimit(string by, int limit)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(new FakeAnalytics()).GetTopProductsAsync(Start, Start.AddDays(1), by, limit));
    }

    [Fact]
    public async Task Pending_AsksForOrdersOlderThanTheMinimumAge_MeasuredFromNow()
    {
        var fake = new FakeAnalytics();
        var clock = new FixedClock(Start);

        await Service(fake, clock).GetPendingAsync(300, 20);

        Assert.Equal((Start.AddSeconds(-300), Start, 20), fake.PendingCall);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(60, 0)]
    [InlineData(60, 101)]
    public async Task Pending_RefusesANegativeAgeAndAnOutOfBoundsLimit(int age, int limit)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service(new FakeAnalytics()).GetPendingAsync(age, limit));
    }

    [Fact]
    public async Task Orders_DefaultToPageOneOf25_AndParseTheStatusIgnoringCase()
    {
        var fake = new FakeAnalytics();

        await Service(fake).GetOrdersAsync(null, Start, Start.AddDays(1), null, null);
        Assert.Equal((null, 1, 25), fake.OrdersCall);

        await Service(fake).GetOrdersAsync("cancelled", Start, Start.AddDays(1), 3, 100);
        Assert.Equal((OrderStatus.Cancelled, 3, 100), fake.OrdersCall);
    }

    [Theory]
    [InlineData("Shipped", 1, 25)]
    [InlineData("7", 1, 25)]
    [InlineData("Pending", 0, 25)]
    [InlineData("Pending", 1, 0)]
    [InlineData("Pending", 1, 101)]
    public async Task Orders_RefuseAnUnknownStatusAndOutOfBoundsPaging(string status, int page, int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(new FakeAnalytics()).GetOrdersAsync(status, Start, Start.AddDays(1), page, pageSize));
    }

    [Fact]
    public async Task TopSelling_ReturnsOnlyProductIds_FromTheLastThirtyDays_ByUnits()
    {
        var fake = new FakeAnalytics();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        fake.TopProducts.Add(new TopProduct(first, 9, 90m));
        fake.TopProducts.Add(new TopProduct(second, 4, 40m));
        var now = Start.AddDays(40);

        var ids = await Service(fake, new FixedClock(now)).GetTopSellingAsync(null);

        Assert.Equal(new[] { first, second }, ids);
        Assert.Equal((TopProductsBy.Units, OrderAnalyticsService.DefaultTopSelling), fake.TopCall);
        Assert.Equal((now.AddDays(-30), now), fake.Calls.Single());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public async Task TopSelling_RejectsALimitOutOfRange(int limit)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service(new FakeAnalytics()).GetTopSellingAsync(limit));
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
