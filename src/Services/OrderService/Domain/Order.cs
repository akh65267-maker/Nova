namespace OrderService.Domain;

public class Order
{
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public List<OrderItem> Items { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>When the order was confirmed or cancelled; null while pending, and for orders resolved before this was recorded.</summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public decimal TotalAmount => Items.Sum(i => i.Quantity * i.UnitPrice);

    private Order()
    {
    }

    public Order(Guid userId, List<OrderItem> items)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));

        if (items is null || items.Count == 0)
            throw new ArgumentException("An order must have at least one item.", nameof(items));

        Id = Guid.NewGuid();
        UserId = userId;
        Items = items;
        Status = OrderStatus.Pending;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Returns whether this call confirmed the order (false if it already was).</summary>
    public bool Confirm()
    {
        // Idempotent: confirming a confirmed order changes nothing. Asking for the opposite
        // outcome is a real conflict, not a repeat, and still throws.
        if (Status == OrderStatus.Confirmed)
            return false;

        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot confirm an order in status '{Status}'.");

        Status = OrderStatus.Confirmed;
        ResolvedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// Idempotent: cancelling a cancelled order changes nothing, and keeps the original reason.
    /// Returns whether this call cancelled it, so a caller can skip side effects (releasing
    /// stock) that belong to the call that really did.
    /// </summary>
    public bool Cancel(string reason)
    {
        if (Status == OrderStatus.Cancelled)
            return false;

        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot cancel an order in status '{Status}'.");

        Status = OrderStatus.Cancelled;
        CancellationReason = reason;
        ResolvedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }
}
