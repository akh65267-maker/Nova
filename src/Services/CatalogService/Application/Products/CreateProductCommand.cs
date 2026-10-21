using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed record CreateProductCommand(string Name, string Description, decimal Price, string Category, string? Brand = null, IReadOnlyList<ProductAttribute>? Attributes = null) : IRequest<Guid>;
