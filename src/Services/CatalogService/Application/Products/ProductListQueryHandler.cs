using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class ProductListQueryHandler : IRequestHandler<ProductListQuery, ProductListResult>
{
    private readonly IProductRepository _repo;

    public ProductListQueryHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<ProductListResult> Handle(ProductListQuery request, CancellationToken cancellationToken)
    {
        var (items, totalCount) = await _repo.SearchAsync(
            request.Category,
            request.MinPrice,
            request.MaxPrice,
            request.Search,
            request.Sort,
            request.Page,
            request.PageSize,
            request.IncludeArchived,
            request.Brand,
            request.OnSale,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var dtos = items.Select(x => ProductDto.From(x, now)).ToList();

        return new ProductListResult(dtos, totalCount, request.Page, request.PageSize);
    }
}
