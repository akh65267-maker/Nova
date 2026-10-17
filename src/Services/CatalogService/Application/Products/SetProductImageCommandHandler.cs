using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class SetProductImageCommandHandler : IRequestHandler<SetProductImageCommand, SetProductImageResult>
{
    private readonly IProductRepository _repo;
    private readonly IProductImageStore _store;
    private readonly ILogger<SetProductImageCommandHandler> _logger;

    public SetProductImageCommandHandler(IProductRepository repo, IProductImageStore store, ILogger<SetProductImageCommandHandler> logger)
    {
        _repo = repo;
        _store = store;
        _logger = logger;
    }

    public async Task<SetProductImageResult> Handle(SetProductImageCommand request, CancellationToken cancellationToken)
    {
        var contentType = ProductImage.DetectContentType(request.Content);
        if (contentType is null || request.Content.Length > ProductImage.MaxBytes)
            return new SetProductImageResult(SetProductImageOutcome.NotAnImage);

        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return new SetProductImageResult(SetProductImageOutcome.ProductNotFound);

        var version = ProductImage.VersionOf(request.Content);
        var previousVersion = product.ImageVersion;

        // The file first, the row second: a row never points at a file that is not there. If the
        // save fails the new file is an orphan, which costs space and nothing else.
        await _store.SaveAsync(product.Id, version, request.Content, cancellationToken);
        product.SetImage(contentType, version);
        await _repo.SaveChangesAsync(cancellationToken);

        if (previousVersion is not null && previousVersion != version)
            _store.Delete(product.Id, previousVersion);

        _logger.LogInformation("Set the picture of product {ProductId} to {Version} ({ContentType}, {Bytes} bytes)",
            product.Id, version, contentType, request.Content.Length);

        return new SetProductImageResult(SetProductImageOutcome.Set, ProductImage.UrlFor(product.Id, version));
    }
}
