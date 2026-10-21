using CatalogService.Application.Categories;
using CatalogService.Application.Products;
using CatalogService.Domain;
using Microsoft.Extensions.Logging;
using Moq;

namespace CatalogService.UnitTests.Application;

public class CategoriesTests
{
    private readonly Mock<ICategoryRepository> _categories = new();
    private readonly Mock<IProductRepository> _products = new();

    [Fact]
    public async Task CreateProduct_InAnUnknownCategory_IsRefused()
    {
        _categories.Setup(x => x.ExistsAsync("Toys", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = new CreateProductCommandHandler(_products.Object, _categories.Object, Mock.Of<ILogger<CreateProductCommandHandler>>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Handle(new CreateProductCommand("Ball", "", 5m, "Toys"), CancellationToken.None));

        _products.Verify(x => x.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProduct_KeepingItsOwnCategory_NeedsNoLookup()
    {
        var product = new Product("Ball", "", 5m, "Old");
        _products.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        var sut = new UpdateProductCommandHandler(_products.Object, _categories.Object, Mock.Of<ILogger<UpdateProductCommandHandler>>());

        Assert.True(await sut.Handle(new UpdateProductCommand(product.Id, "Ball", "", 6m, "Old"), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateProduct_MovedToAnUnknownCategory_IsRefused()
    {
        var product = new Product("Ball", "", 5m, "Old");
        _products.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        var sut = new UpdateProductCommandHandler(_products.Object, _categories.Object, Mock.Of<ILogger<UpdateProductCommandHandler>>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Handle(new UpdateProductCommand(product.Id, "Ball", "", 5m, "Nowhere"), CancellationToken.None));
        Assert.Equal("Old", product.Category);
    }

    [Fact]
    public async Task CreateCategory_WithATakenName_ReportsItAndAddsNothing()
    {
        _categories.Setup(x => x.NameTakenAsync("Toys", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var (outcome, category) = await new CreateCategoryCommandHandler(_categories.Object)
            .Handle(new CreateCategoryCommand("  Toys "), CancellationToken.None);

        Assert.Equal(CategoryOutcome.NameTaken, outcome);
        Assert.Null(category);
        _categories.Verify(x => x.AddAsync(It.IsAny<Category>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateCategory_TrimsTheNameAndSaves()
    {
        var (outcome, category) = await new CreateCategoryCommandHandler(_categories.Object)
            .Handle(new CreateCategoryCommand("  Toys "), CancellationToken.None);

        Assert.Equal(CategoryOutcome.Done, outcome);
        Assert.Equal("Toys", category!.Name);
        _categories.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameCategory_MovesItsProductsToTheNewName()
    {
        var category = new Category("Toys");
        _categories.Setup(x => x.GetByIdAsync(category.Id, It.IsAny<CancellationToken>())).ReturnsAsync(category);

        var outcome = await new RenameCategoryCommandHandler(_categories.Object)
            .Handle(new RenameCategoryCommand(category.Id, "Games"), CancellationToken.None);

        Assert.Equal(CategoryOutcome.Done, outcome);
        Assert.Equal("Games", category.Name);
        _categories.Verify(x => x.RenameProductsAsync("Toys", "Games", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RenameCategory_ToAnotherOnesName_ChangesNothing()
    {
        var category = new Category("Toys");
        _categories.Setup(x => x.GetByIdAsync(category.Id, It.IsAny<CancellationToken>())).ReturnsAsync(category);
        _categories.Setup(x => x.NameTakenAsync("Games", category.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var outcome = await new RenameCategoryCommandHandler(_categories.Object)
            .Handle(new RenameCategoryCommand(category.Id, "Games"), CancellationToken.None);

        Assert.Equal(CategoryOutcome.NameTaken, outcome);
        _categories.Verify(x => x.RenameProductsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _categories.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteCategory_WithProducts_IsRefused()
    {
        var category = new Category("Toys");
        _categories.Setup(x => x.GetByIdAsync(category.Id, It.IsAny<CancellationToken>())).ReturnsAsync(category);
        _categories.Setup(x => x.CountProductsAsync("Toys", It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var outcome = await new DeleteCategoryCommandHandler(_categories.Object)
            .Handle(new DeleteCategoryCommand(category.Id), CancellationToken.None);

        Assert.Equal(CategoryOutcome.InUse, outcome);
        _categories.Verify(x => x.Remove(It.IsAny<Category>()), Times.Never);
    }

    [Fact]
    public async Task DeleteCategory_WhenEmpty_RemovesIt()
    {
        var category = new Category("Toys");
        _categories.Setup(x => x.GetByIdAsync(category.Id, It.IsAny<CancellationToken>())).ReturnsAsync(category);

        var outcome = await new DeleteCategoryCommandHandler(_categories.Object)
            .Handle(new DeleteCategoryCommand(category.Id), CancellationToken.None);

        Assert.Equal(CategoryOutcome.Done, outcome);
        _categories.Verify(x => x.Remove(category), Times.Once);
    }

    [Fact]
    public async Task DeleteCategory_Unknown_IsNotFound()
    {
        Assert.Equal(CategoryOutcome.NotFound, await new DeleteCategoryCommandHandler(_categories.Object)
            .Handle(new DeleteCategoryCommand(Guid.NewGuid()), CancellationToken.None));
    }
}
