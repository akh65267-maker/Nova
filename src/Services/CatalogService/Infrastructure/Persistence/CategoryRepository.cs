using CatalogService.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Persistence;

public class CategoryRepository : ICategoryRepository
{
    private readonly CatalogDbContext _db;

    public CategoryRepository(CatalogDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyCollection<(Category Category, int ProductCount)>> ListAsync(CancellationToken cancellationToken = default)
    {
        var counts = await _db.Products
            .Where(x => x.ArchivedAtUtc == null)
            .GroupBy(x => x.Category)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Name, x => x.Count, cancellationToken);

        var categories = await _db.Categories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

        return categories.Select(c => (c, counts.GetValueOrDefault(c.Name))).ToList();
    }

    public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Categories.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
        => _db.Categories.AnyAsync(x => x.Name == name, cancellationToken);

    public Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default)
        => _db.Categories.AnyAsync(x => x.Id != exceptId && x.Name.ToLower() == name.ToLower(), cancellationToken);

    public Task<int> CountProductsAsync(string name, CancellationToken cancellationToken = default)
        => _db.Products.CountAsync(x => x.Category == name, cancellationToken);

    public Task RenameProductsAsync(string from, string to, CancellationToken cancellationToken = default)
        => _db.Products.Where(x => x.Category == from).ExecuteUpdateAsync(s => s.SetProperty(x => x.Category, to), cancellationToken);

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
        => await _db.Categories.AddAsync(category, cancellationToken);

    public void Remove(Category category) => _db.Categories.Remove(category);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);
}
