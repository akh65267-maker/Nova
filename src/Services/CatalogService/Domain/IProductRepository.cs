namespace CatalogService.Domain;

public interface IProductRepository
{
    Task<IReadOnlyCollection<Product>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<(IReadOnlyCollection<Product> Items, int TotalCount)> SearchAsync(
        string? category,
        decimal? minPrice,
        decimal? maxPrice,
        string? search,
        ProductSortBy sort,
        int page,
        int pageSize,
        bool includeArchived,
        CancellationToken cancellationToken = default);
    Task AddAsync(Product product, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
