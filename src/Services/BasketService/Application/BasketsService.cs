using BasketService.Application.Baskets;
using BasketService.Domain;

namespace BasketService.Application;

public class BasketsService
{
    private readonly IBasketRepository _repo;
    private readonly ILogger<BasketsService> _logger;

    public BasketsService(IBasketRepository repo, ILogger<BasketsService> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<BasketDto> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var basket = await _repo.GetAsync(userId, cancellationToken) ?? new Basket(userId);

        return ToDto(basket);
    }

    public async Task<BasketDto> AddItemAsync(Guid userId, AddBasketItemRequest request, CancellationToken cancellationToken = default)
    {
        var basket = await _repo.GetAsync(userId, cancellationToken) ?? new Basket(userId);

        basket.AddItem(request.ProductId, request.Quantity);

        await _repo.SaveAsync(basket, cancellationToken);

        _logger.LogInformation(
            "Added product {ProductId} x{Quantity} to basket for {UserId}",
            request.ProductId,
            request.Quantity,
            userId);

        return ToDto(basket);
    }

    // The most distinct products and the most of one product a basket may hold; a cart
    // synced from the browser is untrusted input, so it is bounded before it reaches Redis.
    public const int MaxLines = 50;
    public const int MaxQuantity = 99;

    /// <summary>Replaces the whole basket with the given lines (the same product twice is added up).</summary>
    public async Task<BasketDto> ReplaceAsync(Guid userId, ReplaceBasketRequest request, CancellationToken cancellationToken = default)
    {
        var lines = request.Items ?? throw new ArgumentException("Items are required.", nameof(request));
        if (lines.Count > MaxLines)
            throw new ArgumentException($"A basket holds at most {MaxLines} lines.", nameof(request));

        var basket = new Basket(userId);
        foreach (var line in lines)
        {
            if (line.Quantity is < 1 or > MaxQuantity)
                throw new ArgumentException($"Quantity must be between 1 and {MaxQuantity}.", nameof(request));
            basket.AddItem(line.ProductId, line.Quantity);
        }

        if (basket.Items.Any(i => i.Quantity > MaxQuantity))
            throw new ArgumentException($"Quantity must be between 1 and {MaxQuantity}.", nameof(request));

        await _repo.SaveAsync(basket, cancellationToken);

        return ToDto(basket);
    }

    public async Task<BasketDto> RemoveItemAsync(Guid userId, Guid productId, CancellationToken cancellationToken = default)
    {
        var basket = await _repo.GetAsync(userId, cancellationToken) ?? new Basket(userId);

        basket.RemoveItem(productId);

        await _repo.SaveAsync(basket, cancellationToken);

        return ToDto(basket);
    }

    public Task ClearAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return _repo.DeleteAsync(userId, cancellationToken);
    }

    private static BasketDto ToDto(Basket basket)
    {
        return new BasketDto(
            basket.UserId,
            basket.Items.Select(i => new BasketItemDto(i.ProductId, i.Quantity)).ToList());
    }
}
