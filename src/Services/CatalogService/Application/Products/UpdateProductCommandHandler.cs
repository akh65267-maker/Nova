using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class UpdateProductCommandHandler : IRequestHandler<UpdateProductCommand, bool>
{
    private readonly IProductRepository _repo;
    private readonly ILogger<UpdateProductCommandHandler> _logger;

    public UpdateProductCommandHandler(IProductRepository repo, ILogger<UpdateProductCommandHandler> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    public async Task<bool> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (product is null)
            return false;

        product.Update(request.Name, request.Description, request.Price, request.Category);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated product {ProductId}", product.Id);

        return true;
    }
}
