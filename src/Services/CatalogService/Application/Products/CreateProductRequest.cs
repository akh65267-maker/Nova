using CatalogService.Domain;

namespace CatalogService.Application.Products;

public sealed record CreateProductRequest(string Name, string Description, decimal Price, string Category, string? Brand = null, IReadOnlyList<ProductAttribute>? Attributes = null);
