using InventoryService.Application.Inventory;
using InventoryService.Domain;

namespace InventoryService.Application;

public class InventoryItemsService
{
    /// <summary>At or below this many units a product is "low stock".</summary>
    public const int LowStockAt = 5;

    /// <summary>
    /// The exact quantity is shown publicly up to here. The storefront limits an order to 10 units,
    /// so it needs the real number whenever that could be the smaller one; above it the exact
    /// quantity tells a shopper nothing and tells a competitor how much is on the shelf.
    /// </summary>
    public const int ExactUpTo = 10;

    private readonly IInventoryRepository _repo;
    private readonly ILogger<InventoryItemsService> _logger;

    public InventoryItemsService(IInventoryRepository repo, ILogger<InventoryItemsService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<InventoryItemDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await _repo.GetAllAsync(cancellationToken);

        return items.Select(i => new InventoryItemDto(i.ProductId, i.QuantityAvailable)).ToList();
    }

    public async Task<InventoryItemDto?> GetByProductIdAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var item = await _repo.GetByProductIdAsync(productId, cancellationToken);

        return item is null ? null : new InventoryItemDto(item.ProductId, item.QuantityAvailable);
    }

    /// <summary>The public view of one product's stock; see <see cref="AvailabilityDto"/>.</summary>
    public async Task<AvailabilityDto?> GetAvailabilityAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var item = await _repo.GetByProductIdAsync(productId, cancellationToken);
        if (item is null)
            return null;

        var quantity = item.QuantityAvailable;
        var status = quantity <= 0 ? StockStatus.OutOfStock : quantity <= LowStockAt ? StockStatus.LowStock : StockStatus.InStock;

        return new AvailabilityDto(item.ProductId, status, quantity <= ExactUpTo ? Math.Max(quantity, 0) : null);
    }

    // A grid of products asks for every card's stock at once instead of one request per card.
    public const int MaxBatch = 100;

    /// <summary>The public availability of each distinct product asked for; a product with no stock record is left out.</summary>
    public async Task<IReadOnlyList<AvailabilityDto>> GetAvailabilityManyAsync(IReadOnlyCollection<Guid>? productIds, CancellationToken cancellationToken = default)
    {
        var ids = productIds?.Distinct().ToList() ?? [];
        if (ids.Count > MaxBatch)
            throw new ArgumentException($"At most {MaxBatch} products can be asked about at once.", nameof(productIds));

        var found = new List<AvailabilityDto>(ids.Count);
        foreach (var id in ids)
        {
            var availability = await GetAvailabilityAsync(id, cancellationToken);
            if (availability is not null)
                found.Add(availability);
        }

        return found;
    }

    public async Task CreateAsync(CreateInventoryItemRequest request, CancellationToken cancellationToken = default)
    {
        var item = new InventoryItem(request.ProductId, request.QuantityAvailable);

        if (await _repo.ExistsAsync(item.ProductId, cancellationToken))
            throw new ArgumentException($"Inventory for product '{item.ProductId}' already exists.", nameof(request));

        await _repo.AddAsync(item, cancellationToken);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Created inventory for product {ProductId} with quantity {Quantity}",
            item.ProductId,
            item.QuantityAvailable);
    }

    public async Task<InventoryItemDto?> RestockAsync(Guid productId, int quantity, CancellationToken cancellationToken = default)
    {
        // Read-modify-write under the row lock, inside a transaction. A plain read here lets a
        // reservation that commits between the read and the write be overwritten: stock then
        // rises by the restock plus whatever that reservation had taken. The reserve and
        // release consumers take the same lock, so all three writers queue behind each other.
        var item = await _repo.InTransactionAsync(async () =>
        {
            var locked = await _repo.GetByProductIdForUpdateAsync(productId, cancellationToken);
            if (locked is null)
                return null;

            locked.Restock(quantity);
            await _repo.SaveChangesAsync(cancellationToken);
            return locked;
        }, cancellationToken);

        if (item is null)
            return null;

        _logger.LogInformation(
            "Restocked product {ProductId} by {Quantity}, now {NewQuantity}",
            item.ProductId,
            quantity,
            item.QuantityAvailable);

        return new InventoryItemDto(item.ProductId, item.QuantityAvailable);
    }
}
