using OrderService.Domain;

namespace OrderService.Application.Orders;

public sealed record OrderDto(
    Guid Id,
    Guid UserId,
    IReadOnlyCollection<OrderItemDto> Items,
    decimal TotalAmount,
    OrderStatus Status,
    DateTimeOffset CreatedAtUtc,
    string? CancellationReason,
    DateTimeOffset? ResolvedAtUtc);
