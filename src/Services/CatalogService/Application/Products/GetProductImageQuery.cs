using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed record ProductImageContent(Stream Content, string ContentType, string Version);

public sealed record GetProductImageQuery(Guid ProductId) : IRequest<ProductImageContent?>;

public sealed class GetProductImageQueryHandler : IRequestHandler<GetProductImageQuery, ProductImageContent?>
{
    private readonly IProductRepository _repo;
    private readonly IProductImageStore _store;

    public GetProductImageQueryHandler(IProductRepository repo, IProductImageStore store)
    {
        _repo = repo;
        _store = store;
    }

    public async Task<ProductImageContent?> Handle(GetProductImageQuery request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product?.ImageVersion is not { } version || product.ImageContentType is not { } contentType)
            return null;

        var stream = _store.OpenRead(product.Id, version);

        return stream is null ? null : new ProductImageContent(stream, contentType, version);
    }
}
