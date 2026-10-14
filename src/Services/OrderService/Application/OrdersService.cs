using Contracts.Common;
using Contracts.IntegrationEvents;
using MassTransit;
using OrderService.Application.Catalog;
using OrderService.Application.Orders;
using OrderService.Domain;

namespace OrderService.Application;

public class OrdersService
{
    private readonly IOrderRepository _repo;
    private readonly IProductPriceProvider _prices;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<OrdersService> _logger;

    public OrdersService(
        IOrderRepository repo,
        IProductPriceProvider prices,
        IPublishEndpoint publishEndpoint,
        ILogger<OrdersService> logger)
    {
        _repo = repo;
        _prices = prices;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<OrderDto>> GetAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var orders = await _repo.GetAllByUserIdAsync(userId, cancellationToken);

        return orders.Select(ToDto).ToList();
    }

    public async Task<OrderDto?> GetByIdAsync(Guid id, Guid userId, CancellationToken cancellationToken = default)
    {
        var order = await _repo.GetByIdAsync(id, cancellationToken);

        return order is null || order.UserId != userId ? null : ToDto(order);
    }

    /// <summary>Any order, for the admin console. The caller has already been checked for the Admin role.</summary>
    public async Task<OrderDto?> GetForAdminAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await _repo.GetByIdAsync(id, cancellationToken);

        return order is null ? null : ToDto(order);
    }

    public async Task<Guid> CreateAsync(CreateOrderRequest request, Guid userId, CancellationToken cancellationToken = default)
    {
        var items = request.Items
            .Select(i => new OrderItem(i.ProductId, i.Quantity, i.UnitPrice))
            .ToList();

        // Shape first (no items, bad quantity): no point asking the catalog about a request
        // that is invalid anyway.
        var order = new Order(userId, items);

        await EnsurePricesMatchCatalogAsync(order, cancellationToken);

        await _repo.AddAsync(order, cancellationToken);

        await _publishEndpoint.Publish(
            new OrderSubmitted(
                order.Id,
                userId,
                order.Items.Select(i => new OrderLineItem(i.ProductId, i.Quantity, i.UnitPrice)).ToList()),
            cancellationToken);

        // Single SaveChangesAsync commits the order row and the buffered
        // OrderSubmitted outbox message atomically, same pattern (and same
        // reason) as UserService.RegisterAsync: publish before this call,
        // never after, or the message never gets a SaveChanges to flush into.
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created order {OrderId} for user {UserId} with {ItemCount} item(s), total {TotalAmount}",
            order.Id,
            userId,
            order.Items.Count,
            order.TotalAmount);

        return order.Id;
    }

    // The caller sends a price per line; it is a claim to check, never a value to store. A
    // price that disagrees with the catalog is refused rather than silently replaced, so
    // nobody is charged a different amount from the one they were shown.
    private async Task EnsurePricesMatchCatalogAsync(Order order, CancellationToken cancellationToken)
    {
        var catalog = await _prices.GetPricesAsync(
            order.Items.Select(i => i.ProductId).Distinct().ToList(),
            cancellationToken);

        foreach (var item in order.Items)
        {
            if (!catalog.TryGetValue(item.ProductId, out var current))
                throw OrderRejectedException.UnknownProduct(item.ProductId);

            if (item.UnitPrice != current)
                throw OrderRejectedException.PriceChanged(item.ProductId, item.UnitPrice, current);
        }
    }

    private static OrderDto ToDto(Order order)
    {
        return new OrderDto(
            order.Id,
            order.UserId,
            order.Items.Select(i => new OrderItemDto(i.ProductId, i.Quantity, i.UnitPrice)).ToList(),
            order.TotalAmount,
            order.Status,
            order.CreatedAtUtc,
            order.CancellationReason,
            order.ResolvedAtUtc);
    }
}
