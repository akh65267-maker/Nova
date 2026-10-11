using OrderService.Domain;

namespace OrderService.UnitTests.Domain;

public class OrderStatusTransitionTests
{
    private static Order NewPendingOrder() =>
        new(Guid.NewGuid(), new List<OrderItem> { new(Guid.NewGuid(), 1, 9.99m) });

    [Fact]
    public void NewOrder_StartsAsPending()
    {
        var order = NewPendingOrder();

        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void Confirm_FromPending_TransitionsToConfirmed()
    {
        var order = NewPendingOrder();

        order.Confirm();

        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Cancel_FromPending_TransitionsToCancelledWithReason()
    {
        var order = NewPendingOrder();

        order.Cancel("Out of stock.");

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("Out of stock.", order.CancellationReason);
    }

    [Fact]
    public void Confirm_ReportsThatItChangedTheOrder()
    {
        Assert.True(NewPendingOrder().Confirm());
    }

    [Fact]
    public void Cancel_ReportsThatItChangedTheOrder()
    {
        Assert.True(NewPendingOrder().Cancel("Out of stock."));
    }

    [Fact]
    public void Confirm_AlreadyConfirmed_IsANoOp()
    {
        var order = NewPendingOrder();
        order.Confirm();

        var changed = order.Confirm();

        Assert.False(changed);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Cancel_AlreadyCancelled_IsANoOpAndKeepsTheOriginalReason()
    {
        var order = NewPendingOrder();
        order.Cancel("Out of stock.");

        var changed = order.Cancel("Timed out.");

        Assert.False(changed);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal("Out of stock.", order.CancellationReason);
    }

    [Fact]
    public void Cancel_AlreadyConfirmed_StillThrows()
    {
        var order = NewPendingOrder();
        order.Confirm();

        Assert.Throws<InvalidOperationException>(() => order.Cancel("Out of stock."));
        Assert.Equal(OrderStatus.Confirmed, order.Status);
    }

    [Fact]
    public void Confirm_AlreadyCancelled_StillThrows()
    {
        var order = NewPendingOrder();
        order.Cancel("Out of stock.");

        Assert.Throws<InvalidOperationException>(() => order.Confirm());
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public void NewOrder_HasNoResolvedTime()
    {
        Assert.Null(NewPendingOrder().ResolvedAtUtc);
    }

    [Fact]
    public void Confirm_RecordsWhenTheOrderWasResolved()
    {
        var order = NewPendingOrder();
        var before = DateTimeOffset.UtcNow;

        order.Confirm();

        Assert.NotNull(order.ResolvedAtUtc);
        Assert.InRange(order.ResolvedAtUtc!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Cancel_RecordsWhenTheOrderWasResolved()
    {
        var order = NewPendingOrder();
        var before = DateTimeOffset.UtcNow;

        order.Cancel("Out of stock.");

        Assert.NotNull(order.ResolvedAtUtc);
        Assert.InRange(order.ResolvedAtUtc!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RepeatedConfirmAndCancel_NeverOverwriteTheResolvedTime()
    {
        var confirmed = NewPendingOrder();
        confirmed.Confirm();
        var first = confirmed.ResolvedAtUtc;
        confirmed.Confirm();
        Assert.Equal(first, confirmed.ResolvedAtUtc);

        var cancelled = NewPendingOrder();
        cancelled.Cancel("Out of stock.");
        var second = cancelled.ResolvedAtUtc;
        cancelled.Cancel("Timed out.");
        Assert.Equal(second, cancelled.ResolvedAtUtc);
    }

    [Fact]
    public void AConflictingTransition_DoesNotTouchTheResolvedTime()
    {
        var order = NewPendingOrder();
        order.Confirm();
        var resolved = order.ResolvedAtUtc;

        Assert.Throws<InvalidOperationException>(() => order.Cancel("Out of stock."));

        Assert.Equal(resolved, order.ResolvedAtUtc);
    }
}
