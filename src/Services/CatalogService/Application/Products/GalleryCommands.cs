using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public enum AddGalleryImageOutcome { Added, ProductNotFound, NotAnImage, TooMany }

public sealed record AddGalleryImageResult(AddGalleryImageOutcome Outcome, string? Url = null);

public sealed record AddGalleryImageCommand(Guid ProductId, byte[] Content) : IRequest<AddGalleryImageResult>;

/// <summary>Returns false only when the product does not exist: removing a picture that is not there succeeds.</summary>
public sealed record RemoveGalleryImageCommand(Guid ProductId, string Version) : IRequest<bool>;

public sealed record GalleryImageContent(Stream Content, string ContentType);

public sealed record GetGalleryImageQuery(Guid ProductId, string Version) : IRequest<GalleryImageContent?>;

public sealed class AddGalleryImageCommandHandler : IRequestHandler<AddGalleryImageCommand, AddGalleryImageResult>
{
    private readonly IProductRepository _repo;
    private readonly IProductImageStore _store;

    public AddGalleryImageCommandHandler(IProductRepository repo, IProductImageStore store)
    {
        _repo = repo;
        _store = store;
    }

    public async Task<AddGalleryImageResult> Handle(AddGalleryImageCommand request, CancellationToken cancellationToken)
    {
        var contentType = ProductImage.DetectContentType(request.Content);
        if (contentType is null || request.Content.Length > ProductImage.MaxBytes)
            return new AddGalleryImageResult(AddGalleryImageOutcome.NotAnImage);

        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return new AddGalleryImageResult(AddGalleryImageOutcome.ProductNotFound);

        var version = ProductImage.VersionOf(request.Content);
        if (product.UsesPicture(version))
            return new AddGalleryImageResult(AddGalleryImageOutcome.Added, ProductImage.GalleryUrlFor(product.Id, version));

        if (product.Gallery.Count >= GalleryImage.MaxPerProduct)
            return new AddGalleryImageResult(AddGalleryImageOutcome.TooMany);

        // The file first, the row second, as for the main picture.
        await _store.SaveAsync(product.Id, version, request.Content, cancellationToken);
        product.AddGalleryImage(contentType, version);
        await _repo.SaveChangesAsync(cancellationToken);

        return new AddGalleryImageResult(AddGalleryImageOutcome.Added, ProductImage.GalleryUrlFor(product.Id, version));
    }
}

public sealed class RemoveGalleryImageCommandHandler : IRequestHandler<RemoveGalleryImageCommand, bool>
{
    private readonly IProductRepository _repo;
    private readonly IProductImageStore _store;

    public RemoveGalleryImageCommandHandler(IProductRepository repo, IProductImageStore store)
    {
        _repo = repo;
        _store = store;
    }

    public async Task<bool> Handle(RemoveGalleryImageCommand request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        if (product.RemoveGalleryImage(request.Version))
        {
            await _repo.SaveChangesAsync(cancellationToken);

            // The same bytes may also be the main picture, which keeps the file.
            if (!product.UsesPicture(request.Version))
                _store.Delete(product.Id, request.Version);
        }

        return true;
    }
}

public sealed class GetGalleryImageQueryHandler : IRequestHandler<GetGalleryImageQuery, GalleryImageContent?>
{
    private readonly IProductRepository _repo;
    private readonly IProductImageStore _store;

    public GetGalleryImageQueryHandler(IProductRepository repo, IProductImageStore store)
    {
        _repo = repo;
        _store = store;
    }

    public async Task<GalleryImageContent?> Handle(GetGalleryImageQuery request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        var image = product?.Gallery.FirstOrDefault(x => x.Version == request.Version);
        if (product is null || image is null)
            return null;

        var stream = _store.OpenRead(product.Id, image.Version);

        return stream is null ? null : new GalleryImageContent(stream, image.ContentType);
    }
}
