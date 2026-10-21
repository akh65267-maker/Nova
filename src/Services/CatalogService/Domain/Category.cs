namespace CatalogService.Domain;

public class Category
{
    public const int MaxNameLength = 100;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private Category()
    {
    }

    public Category(string name)
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Name = Normalize(name);
    }

    public void Rename(string name) => Name = Normalize(name);

    private static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name is required.", nameof(name));

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
            throw new ArgumentException($"Category name can be at most {MaxNameLength} characters.", nameof(name));

        return trimmed;
    }
}
