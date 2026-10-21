using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed record BrandDto(string Name, int ProductCount);

public sealed record ListBrandsQuery : IRequest<IReadOnlyCollection<BrandDto>>;

public sealed class ListBrandsQueryHandler : IRequestHandler<ListBrandsQuery, IReadOnlyCollection<BrandDto>>
{
    private readonly IProductRepository _repo;

    public ListBrandsQueryHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<IReadOnlyCollection<BrandDto>> Handle(ListBrandsQuery request, CancellationToken cancellationToken)
    {
        var brands = await _repo.ListBrandsAsync(cancellationToken);

        return brands.Select(x => new BrandDto(x.Brand, x.ProductCount)).ToList();
    }
}
