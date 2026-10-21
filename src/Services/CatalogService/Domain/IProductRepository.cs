namespace CatalogService.Domain;

public interface IProductRepository
{
    Task<IReadOnlyCollection<Product>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <remarks>Prices, in filters and sorts, are what customers pay now; <paramref name="onSale"/> keeps products with an active discount.</remarks>
    Task<(IReadOnlyCollection<Product> Items, int TotalCount)> SearchAsync(
        string? category,
        decimal? minPrice,
        decimal? maxPrice,
        string? search,
        ProductSortBy sort,
        int page,
        int pageSize,
        bool includeArchived,
        string? brand,
        bool onSale,
        CancellationToken cancellationToken = default);
    /// <summary>The brands of products on sale, by name, with how many products each has.</summary>
    Task<IReadOnlyCollection<(string Brand, int ProductCount)>> ListBrandsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Products on sale like this one: the same brand or the same category, never the product itself.
    /// Those of the same brand and category come first, then the same brand, then the newest.
    /// </summary>
    Task<IReadOnlyCollection<Product>> ListRelatedAsync(Guid productId, string category, string? brand, int limit, CancellationToken cancellationToken = default);
    Task AddAsync(Product product, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
