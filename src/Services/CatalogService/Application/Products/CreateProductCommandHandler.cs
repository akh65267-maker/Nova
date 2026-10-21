using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed class CreateProductCommandHandler : IRequestHandler<CreateProductCommand, Guid>
{
    private readonly IProductRepository _repo;
    private readonly ICategoryRepository _categories;
    private readonly ILogger<CreateProductCommandHandler> _logger;

    public CreateProductCommandHandler(IProductRepository repo, ICategoryRepository categories, ILogger<CreateProductCommandHandler> logger)
    {
        _repo = repo;
        _categories = categories;
        _logger = logger;
    }

    public async Task<Guid> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var product = new Product(request.Name, request.Description, request.Price, request.Category);

        if (!await _categories.ExistsAsync(product.Category, cancellationToken))
            throw new ArgumentException($"Unknown category '{product.Category}'.", nameof(request));

        await _repo.AddAsync(product, cancellationToken);
        await _repo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created product {ProductId} with name {Name}", product.Id, product.Name);

        return product.Id;
    }
}
