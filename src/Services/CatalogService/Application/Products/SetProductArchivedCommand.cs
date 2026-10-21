using MediatR;

namespace CatalogService.Application.Products;

/// <summary>Archives or restores a product. Returns false when there is no such product.</summary>
public sealed record SetProductArchivedCommand(Guid Id, bool Archived) : IRequest<bool>;
