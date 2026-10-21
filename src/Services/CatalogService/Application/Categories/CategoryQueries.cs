using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Categories;

public sealed record CategoryRequest(string Name);

public sealed record CategoryDto(Guid Id, string Name, int ProductCount);

public sealed record ListCategoriesQuery : IRequest<IReadOnlyCollection<CategoryDto>>;

public sealed class ListCategoriesQueryHandler : IRequestHandler<ListCategoriesQuery, IReadOnlyCollection<CategoryDto>>
{
    private readonly ICategoryRepository _repo;

    public ListCategoriesQueryHandler(ICategoryRepository repo)
    {
        _repo = repo;
    }

    public async Task<IReadOnlyCollection<CategoryDto>> Handle(ListCategoriesQuery request, CancellationToken cancellationToken)
    {
        var categories = await _repo.ListAsync(cancellationToken);

        return categories.Select(x => new CategoryDto(x.Category.Id, x.Category.Name, x.ProductCount)).ToList();
    }
}
