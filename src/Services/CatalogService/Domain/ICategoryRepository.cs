namespace CatalogService.Domain;

public interface ICategoryRepository
{
    /// <summary>Every category by name, with the number of products on sale in it.</summary>
    Task<IReadOnlyCollection<(Category Category, int ProductCount)>> ListAsync(CancellationToken cancellationToken = default);
    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);
    /// <summary>Whether another category already has this name, ignoring case.</summary>
    Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default);
    /// <summary>The products in a category, archived ones included.</summary>
    Task<int> CountProductsAsync(string name, CancellationToken cancellationToken = default);
    Task RenameProductsAsync(string from, string to, CancellationToken cancellationToken = default);
    Task AddAsync(Category category, CancellationToken cancellationToken = default);
    void Remove(Category category);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
