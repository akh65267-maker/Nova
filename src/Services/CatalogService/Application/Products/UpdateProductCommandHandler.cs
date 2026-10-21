using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class UpdateProductCommandHandler : IRequestHandler<UpdateProductCommand, bool>
{
    private readonly IProductRepository _repo;
    private readonly ICategoryRepository _categories;
    private readonly ILogger<UpdateProductCommandHandler> _logger;

    public UpdateProductCommandHandler(IProductRepository repo, ICategoryRepository categories, ILogger<UpdateProductCommandHandler> logger)
    {
        _repo = repo;
        _categories = categories;
        _logger = logger;
    }

    public async Task<bool> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (product is null)
            return false;

        // A product may keep the category it has, even one that has since been removed from the list.
        if (request.Category != product.Category && !await _categories.ExistsAsync(request.Category, cancellationToken))
            throw new ArgumentException($"Unknown category '{request.Category}'.", nameof(request));

        product.Update(request.Name, request.Description, request.Price, request.Category, request.Brand);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated product {ProductId}", product.Id);

        return true;
    }
}
