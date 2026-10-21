using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class GetProductByIdQueryHandler : IRequestHandler<GetProductByIdQuery, ProductDto?>
{
    private readonly IProductRepository _repo;

    public GetProductByIdQueryHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<ProductDto?> Handle(GetProductByIdQuery request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.Id, cancellationToken);

        return product is null || (product.IsArchived && !request.IncludeArchived) ? null : ProductDto.From(product);
    }
}
