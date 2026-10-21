using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed record DiscountDto(string Kind, decimal Value, DateTimeOffset? StartsAtUtc, DateTimeOffset? EndsAtUtc);

/// <summary>The product's discount as set, active or not. Null when the product has none.</summary>
public sealed record GetDiscountQuery(Guid ProductId) : IRequest<DiscountLookup>;

public sealed record DiscountLookup(bool ProductFound, DiscountDto? Discount);

/// <summary>Returns false when there is no such product. Throws ArgumentException for a discount that is not valid.</summary>
public sealed record SetDiscountCommand(Guid ProductId, string Kind, decimal Value, DateTimeOffset? StartsAtUtc, DateTimeOffset? EndsAtUtc) : IRequest<bool>;

/// <summary>Returns false when there is no such product: removing a discount that is not there succeeds.</summary>
public sealed record ClearDiscountCommand(Guid ProductId) : IRequest<bool>;

public sealed class GetDiscountQueryHandler : IRequestHandler<GetDiscountQuery, DiscountLookup>
{
    private readonly IProductRepository _repo;

    public GetDiscountQueryHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<DiscountLookup> Handle(GetDiscountQuery request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);

        return product is null
            ? new DiscountLookup(false, null)
            : new DiscountLookup(true, product.Discount is { } d ? new DiscountDto(d.Kind.ToString(), d.Value, d.StartsAtUtc, d.EndsAtUtc) : null);
    }
}

public sealed class SetDiscountCommandHandler : IRequestHandler<SetDiscountCommand, bool>
{
    private readonly IProductRepository _repo;

    public SetDiscountCommandHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<bool> Handle(SetDiscountCommand request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<DiscountKind>(request.Kind, ignoreCase: true, out var kind))
            throw new ArgumentException("The discount must be a percent or a fixed amount.", nameof(request));

        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        product.SetDiscount(new Discount(kind, request.Value, request.StartsAtUtc, request.EndsAtUtc));
        await _repo.SaveChangesAsync(cancellationToken);

        return true;
    }
}

public sealed class ClearDiscountCommandHandler : IRequestHandler<ClearDiscountCommand, bool>
{
    private readonly IProductRepository _repo;

    public ClearDiscountCommandHandler(IProductRepository repo)
    {
        _repo = repo;
    }

    public async Task<bool> Handle(ClearDiscountCommand request, CancellationToken cancellationToken)
    {
        var product = await _repo.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        if (product.Discount is not null)
        {
            product.ClearDiscount();
            await _repo.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
