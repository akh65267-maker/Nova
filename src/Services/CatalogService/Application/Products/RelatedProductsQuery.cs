using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

/// <summary>The products to suggest beside one. Null when that product does not exist or is off sale.</summary>
public sealed record RelatedProductsQuery(Guid ProductId, int Limit = 4) : IRequest<IReadOnlyCollection<ProductDto>?>;

public sealed class RelatedProductsQueryHandler : IRequestHandler<RelatedProductsQuery, IReadOnlyCollection<ProductDto>?>
{
    private readonly IProductRepository _repo;

    public RelatedProductsQueryHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<IReadOnlyCollection<ProductDto>?> Handle(RelatedProductsQuery request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null || product.IsArchived)
            return null;

        var related = await _repo.ListRelatedAsync(product.Id, product.Category, product.Brand, request.Limit, cancellationToken);

        return related.Select(ProductDto.From).ToList();
    }
}
