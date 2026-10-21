using CatalogService.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Persistence;

public class ProductRepository : IProductRepository
{
    private readonly CatalogDbContext _db;

    public ProductRepository(CatalogDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<Product>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _db.Products
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Products
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<(IReadOnlyCollection<Product> Items, int TotalCount)> SearchAsync(
        string? category,
        decimal? minPrice,
        decimal? maxPrice,
        string? search,
        ProductSortBy sort,
        int page,
        int pageSize,
        bool includeArchived,
        string? brand,
        CancellationToken cancellationToken = default)
    {
        var query = _db.Products.AsNoTracking().AsQueryable();

        if (!includeArchived)
            query = query.Where(x => x.ArchivedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(brand))
            query = query.Where(x => x.Brand == brand);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(x => x.Category == category);

        if (minPrice.HasValue)
            query = query.Where(x => x.Price >= minPrice.Value);

        if (maxPrice.HasValue)
            query = query.Where(x => x.Price <= maxPrice.Value);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => EF.Functions.ILike(x.Name, $"%{search}%") || (x.Brand != null && EF.Functions.ILike(x.Brand, $"%{search}%")));

        query = sort switch
        {
            ProductSortBy.PriceAscending => query.OrderBy(x => x.Price),
            ProductSortBy.PriceDescending => query.OrderByDescending(x => x.Price),
            ProductSortBy.NameAscending => query.OrderBy(x => x.Name),
            _ => query.OrderByDescending(x => x.CreatedAtUtc),
        };

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IReadOnlyCollection<(string Brand, int ProductCount)>> ListBrandsAsync(CancellationToken cancellationToken = default)
    {
        var brands = await _db.Products
            .Where(x => x.ArchivedAtUtc == null && x.Brand != null)
            .GroupBy(x => x.Brand!)
            .Select(g => new { Brand = g.Key, Count = g.Count() })
            .OrderBy(x => x.Brand)
            .ToListAsync(cancellationToken);

        return brands.Select(x => (x.Brand, x.Count)).ToList();
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        await _db.Products.AddAsync(product, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return _db.SaveChangesAsync(cancellationToken);
    }
}
