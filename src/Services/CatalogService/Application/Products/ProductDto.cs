using CatalogService.Domain;

namespace CatalogService.Application.Products;

/// <param name="ImageUrl">Where the picture is, or null when the product has none. The URL carries the picture's version, so it changes when the picture does.</param>
public sealed record ProductDto(Guid Id, string Name, string Description, decimal Price, string Category, string? ImageUrl = null)
{
    public static ProductDto From(Product p) => new(
        p.Id, p.Name, p.Description, p.Price, p.Category,
        p.ImageVersion is { } version ? ProductImage.UrlFor(p.Id, version) : null);
}
