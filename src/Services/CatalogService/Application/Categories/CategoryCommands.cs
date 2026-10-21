using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Categories;

public enum CategoryOutcome
{
    Done,
    NotFound,
    NameTaken,
    InUse,
}

public sealed record CreateCategoryCommand(string Name) : IRequest<(CategoryOutcome Outcome, CategoryDto? Category)>;

public sealed record RenameCategoryCommand(Guid Id, string Name) : IRequest<CategoryOutcome>;

public sealed record DeleteCategoryCommand(Guid Id) : IRequest<CategoryOutcome>;

public sealed class CreateCategoryCommandHandler : IRequestHandler<CreateCategoryCommand, (CategoryOutcome Outcome, CategoryDto? Category)>
{
    private readonly ICategoryRepository _repo;

    public CreateCategoryCommandHandler(ICategoryRepository repo)
    {
        _repo = repo;
    }

    public async Task<(CategoryOutcome Outcome, CategoryDto? Category)> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = new Category(request.Name);

        if (await _repo.NameTakenAsync(category.Name, null, cancellationToken))
            return (CategoryOutcome.NameTaken, null);

        await _repo.AddAsync(category, cancellationToken);
        await _repo.SaveChangesAsync(cancellationToken);

        return (CategoryOutcome.Done, new CategoryDto(category.Id, category.Name, 0));
    }
}

public sealed class RenameCategoryCommandHandler : IRequestHandler<RenameCategoryCommand, CategoryOutcome>
{
    private readonly ICategoryRepository _repo;

    public RenameCategoryCommandHandler(ICategoryRepository repo)
    {
        _repo = repo;
    }

    public async Task<CategoryOutcome> Handle(RenameCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (category is null)
            return CategoryOutcome.NotFound;

        var previous = category.Name;
        category.Rename(request.Name);

        if (await _repo.NameTakenAsync(category.Name, category.Id, cancellationToken))
            return CategoryOutcome.NameTaken;

        // Products carry the category name, so they follow the rename.
        if (previous != category.Name)
            await _repo.RenameProductsAsync(previous, category.Name, cancellationToken);

        await _repo.SaveChangesAsync(cancellationToken);

        return CategoryOutcome.Done;
    }
}

public sealed class DeleteCategoryCommandHandler : IRequestHandler<DeleteCategoryCommand, CategoryOutcome>
{
    private readonly ICategoryRepository _repo;

    public DeleteCategoryCommandHandler(ICategoryRepository repo)
    {
        _repo = repo;
    }

    public async Task<CategoryOutcome> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await _repo.GetByIdAsync(request.Id, cancellationToken);
        if (category is null)
            return CategoryOutcome.NotFound;

        if (await _repo.CountProductsAsync(category.Name, cancellationToken) > 0)
            return CategoryOutcome.InUse;

        _repo.Remove(category);
        await _repo.SaveChangesAsync(cancellationToken);

        return CategoryOutcome.Done;
    }
}
