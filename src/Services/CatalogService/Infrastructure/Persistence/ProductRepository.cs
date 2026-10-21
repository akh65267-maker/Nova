using System.Linq.Expressions;
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
        bool onSale,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var query = _db.Products.AsNoTracking().AsQueryable();

        // What a customer pays now: the price, less the discount while it is on. Mirrors Product.EffectivePrice.
        Expression<Func<Product, decimal>> effectivePrice = x =>
            x.Discount == null
            || (x.Discount.StartsAtUtc != null && x.Discount.StartsAtUtc > now)
            || (x.Discount.EndsAtUtc != null && x.Discount.EndsAtUtc <= now)
                ? x.Price
                : x.Discount.Kind == DiscountKind.Percent
                    ? Math.Round(x.Price * (100 - x.Discount.Value) / 100, 2)
                    : x.Discount.Value < x.Price ? x.Price - x.Discount.Value : x.Price;

        if (!includeArchived)
            query = query.Where(x => x.ArchivedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(brand))
            query = query.Where(x => x.Brand == brand);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(x => x.Category == category);

        if (minPrice.HasValue)
            query = WherePrice(query, effectivePrice, price => price >= minPrice.Value);

        if (maxPrice.HasValue)
            query = WherePrice(query, effectivePrice, price => price <= maxPrice.Value);

        if (onSale)
            query = query.Where(x => x.Discount != null
                && (x.Discount.StartsAtUtc == null || x.Discount.StartsAtUtc <= now)
                && (x.Discount.EndsAtUtc == null || now < x.Discount.EndsAtUtc)
                && (x.Discount.Kind == DiscountKind.Percent || x.Discount.Value < x.Price));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => EF.Functions.ILike(x.Name, $"%{search}%") || (x.Brand != null && EF.Functions.ILike(x.Brand, $"%{search}%")));

        query = sort switch
        {
            ProductSortBy.PriceAscending => query.OrderBy(effectivePrice),
            ProductSortBy.PriceDescending => query.OrderByDescending(effectivePrice),
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

    // Filters on the effective price: puts the price expression in place of the parameter of the test.
    private static IQueryable<Product> WherePrice(IQueryable<Product> query, Expression<Func<Product, decimal>> price, Expression<Func<decimal, bool>> test)
    {
        var body = new ReplaceParameter(test.Parameters[0], price.Body).Visit(test.Body);

        return query.Where(Expression.Lambda<Func<Product, bool>>(body, price.Parameters));
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
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

    public async Task<IReadOnlyCollection<Product>> ListRelatedAsync(Guid productId, string category, string? brand, int limit, CancellationToken cancellationToken = default)
    {
        return await _db.Products
            .AsNoTracking()
            .Where(x => x.Id != productId && x.ArchivedAtUtc == null
                && (x.Category == category || (brand != null && x.Brand == brand)))
            .OrderByDescending(x => (x.Category == category ? 1 : 0) + (brand != null && x.Brand == brand ? 2 : 0))
            .ThenByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);
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
