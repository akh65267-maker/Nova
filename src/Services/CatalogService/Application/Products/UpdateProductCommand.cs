using MediatR;

namespace CatalogService.Application.Products;

/// <summary>Returns false when there is no such product.</summary>
public sealed record UpdateProductCommand(Guid Id, string Name, string Description, decimal Price, string Category, string? Brand = null) : IRequest<bool>;
