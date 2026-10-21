using CatalogService.Domain;

namespace CatalogService.Application.Products;

/// <param name="IsArchived">Off sale: only an admin sees an archived product.</param>
/// <param name="Price">What the customer pays now: the list price less an active discount.</param>
/// <param name="ListPrice">The price before any discount.</param>
/// <param name="DiscountPercent">How much cheaper the product is now, rounded; null when no discount is active.</param>
/// <param name="ImageUrl">Where the picture is, or null when the product has none. The URL carries the picture's version, so it changes when the picture does.</param>
public sealed record ProductDto(Guid Id, string Name, string Description, decimal Price, string Category, string? ImageUrl = null, bool IsArchived = false, string? Brand = null, IReadOnlyList<ProductAttribute>? Attributes = null, IReadOnlyList<string>? Gallery = null, double? RatingAverage = null, int RatingCount = 0, decimal? ListPrice = null, int? DiscountPercent = null, DateTimeOffset? DiscountEndsAtUtc = null)
{
    public static ProductDto From(Product p) => From(p, DateTimeOffset.UtcNow);

    public static ProductDto From(Product p, DateTimeOffset now)
    {
        var price = p.EffectivePrice(now);
        var onSale = price < p.Price;

        return new(
        p.Id, p.Name, p.Description, price, p.Category,
        p.ImageVersion is { } version ? ProductImage.UrlFor(p.Id, version) : null,
        p.IsArchived, p.Brand, p.Attributes,
        p.Gallery.Select(x => ProductImage.GalleryUrlFor(p.Id, x.Version)).ToList(),
        p.RatingAverage, p.RatingCount,
        p.Price,
        onSale ? Math.Max(1, (int)Math.Round((p.Price - price) / p.Price * 100, MidpointRounding.AwayFromZero)) : null,
        onSale ? p.Discount!.EndsAtUtc : null);
    }
}
