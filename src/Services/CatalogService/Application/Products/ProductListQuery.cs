using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Products;

public sealed record ProductListQuery(
    string? Category,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Search,
    ProductSortBy Sort,
    int Page,
    int PageSize,
    bool IncludeArchived = false,
    string? Brand = null,
    bool OnSale = false,
    IReadOnlyCollection<Guid>? Ids = null) : IRequest<ProductListResult>;
