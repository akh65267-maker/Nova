using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

/// <summary>Removes a product's picture. Returns false only when the product does not exist: removing a picture that is not there succeeds.</summary>
public sealed record ClearProductImageCommand(Guid ProductId) : IRequest<bool>;

public sealed class ClearProductImageCommandHandler : IRequestHandler<ClearProductImageCommand, bool>
{
    private readonly IProductRepository _repo;
    private readonly IProductImageStore _store;

    public ClearProductImageCommandHandler(IProductRepository repo, IProductImageStore store)
    {
        _repo = repo;
        _store = store;
    }

    public async Task<bool> Handle(ClearProductImageCommand request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        var version = product.ImageVersion;
        if (version is null)
            return true;

        product.ClearImage();
        await _repo.SaveChangesAsync(cancellationToken);
        _store.Delete(product.Id, version);

        return true;
    }
}
