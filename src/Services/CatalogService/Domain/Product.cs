namespace CatalogService.Domain;

public class Product
{
    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public decimal Price { get; private set; }
    public string Category { get; private set; }
    public string? Brand { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? ImageContentType { get; private set; }
    public string? ImageVersion { get; private set; }
    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    public Discount? Discount { get; private set; }

    private readonly List<ProductAttribute> _attributes = new();
    public IReadOnlyList<ProductAttribute> Attributes => _attributes;

    private readonly List<GalleryImage> _gallery = new();
    public IReadOnlyList<GalleryImage> Gallery => _gallery;

    private Product()
    {
    }

    public Product(string name, string description, decimal price, string category, string? brand = null, IEnumerable<ProductAttribute>? attributes = null)
    {
        Id = Guid.NewGuid();
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Update(name, description, price, category, brand, attributes);
    }

    public bool IsArchived => ArchivedAtUtc is not null;

    public const int MaxBrandLength = 100;

    /// <param name="attributes">Replaces the product's attributes; null leaves them as they are.</param>
    public void Update(string name, string description, decimal price, string category, string? brand = null, IEnumerable<ProductAttribute>? attributes = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Product name is required.", nameof(name));

        if (price < 0)
            throw new ArgumentException("Price cannot be negative.", nameof(price));

        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Product category is required.", nameof(category));

        var trimmedBrand = string.IsNullOrWhiteSpace(brand) ? null : brand.Trim();
        if (trimmedBrand is { Length: > MaxBrandLength })
            throw new ArgumentException($"Brand can be at most {MaxBrandLength} characters.", nameof(brand));

        var cleaned = attributes is null ? null : CleanAttributes(attributes);

        Name = name;
        Description = description ?? string.Empty;
        Price = price;
        Category = category;
        Brand = trimmedBrand;

        if (cleaned is not null)
        {
            _attributes.Clear();
            _attributes.AddRange(cleaned);
        }
    }

    // Blank rows are dropped, a name repeated keeps its last value, and the limits are enforced.
    private static List<ProductAttribute> CleanAttributes(IEnumerable<ProductAttribute> attributes)
    {
        var byName = new Dictionary<string, ProductAttribute>(StringComparer.OrdinalIgnoreCase);

        foreach (var attribute in attributes)
        {
            var name = attribute.Name?.Trim() ?? string.Empty;
            var value = attribute.Value?.Trim() ?? string.Empty;
            if (name.Length == 0 && value.Length == 0)
                continue;

            if (name.Length == 0 || value.Length == 0)
                throw new ArgumentException("Each attribute needs both a name and a value.", nameof(attributes));
            if (name.Length > ProductAttribute.MaxNameLength)
                throw new ArgumentException($"An attribute name can be at most {ProductAttribute.MaxNameLength} characters.", nameof(attributes));
            if (value.Length > ProductAttribute.MaxValueLength)
                throw new ArgumentException($"An attribute value can be at most {ProductAttribute.MaxValueLength} characters.", nameof(attributes));

            byName[name] = new ProductAttribute(name, value);
        }

        if (byName.Count > ProductAttribute.MaxPerProduct)
            throw new ArgumentException($"A product can have at most {ProductAttribute.MaxPerProduct} attributes.", nameof(attributes));

        return byName.Values.ToList();
    }

    /// <summary>The price a customer pays at this moment: the price, less the discount while it is on.</summary>
    public decimal EffectivePrice(DateTimeOffset now) => Discount is { } d && d.IsActiveAt(now) ? d.Apply(Price) : Price;

    public void SetDiscount(Discount discount)
    {
        if (!Enum.IsDefined(discount.Kind))
            throw new ArgumentException("The discount must be a percent or a fixed amount.", nameof(discount));

        if (discount.Kind == DiscountKind.Percent && discount.Value is <= 0 or >= 100)
            throw new ArgumentException("A percent discount must be above 0 and below 100.", nameof(discount));

        if (discount.Kind == DiscountKind.Fixed && (discount.Value <= 0 || discount.Value >= Price))
            throw new ArgumentException("A fixed discount must be above 0 and below the price.", nameof(discount));

        if (discount.StartsAtUtc is { } starts && discount.EndsAtUtc is { } ends && ends <= starts)
            throw new ArgumentException("The discount must end after it starts.", nameof(discount));

        Discount = discount;
    }

    public void ClearDiscount() => Discount = null;

    /// <summary>Takes the product off sale. It is kept, not deleted: orders still refer to it.</summary>
    public void Archive() => ArchivedAtUtc ??= DateTimeOffset.UtcNow;

    public void Restore() => ArchivedAtUtc = null;

    public void SetImage(string contentType, string version)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("The picture's content type is required.", nameof(contentType));

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("The picture's version is required.", nameof(version));

        ImageContentType = contentType;
        ImageVersion = version;
    }

    /// <summary>Returns false when the product already has this picture, as its main one or in the gallery.</summary>
    public bool AddGalleryImage(string contentType, string version)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            throw new ArgumentException("The picture's content type is required.", nameof(contentType));

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("The picture's version is required.", nameof(version));

        if (UsesPicture(version))
            return false;

        if (_gallery.Count >= GalleryImage.MaxPerProduct)
            throw new InvalidOperationException($"A product can have at most {GalleryImage.MaxPerProduct} gallery pictures.");

        _gallery.Add(new GalleryImage(version, contentType));
        return true;
    }

    public bool RemoveGalleryImage(string version) => _gallery.RemoveAll(x => x.Version == version) > 0;

    /// <summary>Whether the picture file of this version belongs to the product, as its main picture or in the gallery.</summary>
    public bool UsesPicture(string version) => ImageVersion == version || _gallery.Any(x => x.Version == version);

    public void ClearImage()
    {
        ImageContentType = null;
        ImageVersion = null;
    }
}
