namespace CatalogService.Domain;

public class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public decimal Price { get; private set; }
    public string Category { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? ImageContentType { get; private set; }
    public string? ImageVersion { get; private set; }

    private Product()
    {
    }

    public Product(string name, string description, decimal price, string category)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Product name is required.", nameof(name));

        if (price < 0)
            throw new ArgumentException("Price cannot be negative.", nameof(price));

        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Product category is required.", nameof(category));

        Id = Guid.NewGuid();
        Name = name;
        Description = description ?? string.Empty;
        Price = price;
        Category = category;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void SetImage(string contentType, string version)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("The picture's content type is required.", nameof(contentType));

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("The picture's version is required.", nameof(version));

        ImageContentType = contentType;
        ImageVersion = version;
    }

    public void ClearImage()
    {
        ImageContentType = null;
        ImageVersion = null;
    }
}
