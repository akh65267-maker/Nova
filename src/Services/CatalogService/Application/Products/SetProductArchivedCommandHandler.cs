using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class SetProductArchivedCommandHandler : IRequestHandler<SetProductArchivedCommand, bool>
{
    private readonly IProductRepository _repo;
    private readonly ILogger<SetProductArchivedCommandHandler> _logger;

    public SetProductArchivedCommandHandler(IProductRepository repo, ILogger<SetProductArchivedCommandHandler> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<bool> Handle(SetProductArchivedCommand request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (product is null)
            return false;

        if (request.Archived)
            product.Archive();
        else
            product.Restore();
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("{Action} product {ProductId}", request.Archived ? "Archived" : "Restored", product.Id);

        return true;
    }
}
