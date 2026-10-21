using CatalogService.Application.Products;
using CatalogService.Domain;
using Microsoft.Extensions.Logging;
using Moq;

namespace CatalogService.UnitTests.Application;

public class ProductsServiceTests
{
    private readonly Mock<IProductRepository> _repo = new();
    private readonly Mock<ICategoryRepository> _categories = new();

    public ProductsServiceTests()
    {
        _categories.Setup(x => x.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    [Fact]
    public async Task CreateProductCommand_WithInvalidName_ThrowsWithoutAdding()
    {
        var sut = new CreateProductCommandHandler(_repo.Object, _categories.Object, Mock.Of<ILogger<CreateProductCommandHandler>>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Handle(new CreateProductCommand("", "desc", 9.99m, "Electronics"), CancellationToken.None));

        _repo.Verify(x => x.AddAsync(It.IsAny<Product>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateProductCommand_WithValidRequest_AddsAndSavesAndReturnsId()
    {
        var sut = new CreateProductCommandHandler(_repo.Object, _categories.Object, Mock.Of<ILogger<CreateProductCommandHandler>>());

        var id = await sut.Handle(new CreateProductCommand("Widget", "desc", 9.99m, "Electronics"), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
        _repo.Verify(x => x.AddAsync(
            It.Is<Product>(p => p.Name == "Widget" && p.Price == 9.99m && p.Category == "Electronics"),
            It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetProductByIdQuery_NotFound_ReturnsNull()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Product?)null);

        var sut = new GetProductByIdQueryHandler(_repo.Object);

        var result = await sut.Handle(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProductListQuery_ReturnsPagedResultFromRepository()
    {
        var product = new Product("Widget", "desc", 9.99m, "Electronics");
        _repo.Setup(x => x.SearchAsync(
                "Electronics", null, null, null, ProductSortBy.Newest, 1, 20, false, null, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { product }, 1));

        var sut = new ProductListQueryHandler(_repo.Object);

        var result = await sut.Handle(
            new ProductListQuery("Electronics", null, null, null, ProductSortBy.Newest, 1, 20),
            CancellationToken.None);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Items);
        Assert.Equal("Widget", result.Items.First().Name);
    }

    [Fact]
    public async Task UpdateProductCommand_ChangesTheProductAndSaves()
    {
        var product = new Product("Widget", "desc", 9.99m, "Electronics");
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        var sut = new UpdateProductCommandHandler(_repo.Object, _categories.Object, Mock.Of<ILogger<UpdateProductCommandHandler>>());

        var found = await sut.Handle(new UpdateProductCommand(product.Id, "Gadget", "new", 12m, "Home"), CancellationToken.None);

        Assert.True(found);
        Assert.Equal(("Gadget", "new", 12m, "Home"), (product.Name, product.Description, product.Price, product.Category));
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProductCommand_WithInvalidPrice_ThrowsWithoutSaving()
    {
        var product = new Product("Widget", "desc", 9.99m, "Electronics");
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        var sut = new UpdateProductCommandHandler(_repo.Object, _categories.Object, Mock.Of<ILogger<UpdateProductCommandHandler>>());

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.Handle(new UpdateProductCommand(product.Id, "Widget", "desc", -1m, "Electronics"), CancellationToken.None));

        Assert.Equal(9.99m, product.Price);
        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAndArchive_OfAMissingProduct_ReportNotFound()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Product?)null);

        Assert.False(await new UpdateProductCommandHandler(_repo.Object, _categories.Object, Mock.Of<ILogger<UpdateProductCommandHandler>>())
            .Handle(new UpdateProductCommand(Guid.NewGuid(), "A", "b", 1m, "C"), CancellationToken.None));
        Assert.False(await new SetProductArchivedCommandHandler(_repo.Object, Mock.Of<ILogger<SetProductArchivedCommandHandler>>())
            .Handle(new SetProductArchivedCommand(Guid.NewGuid(), true), CancellationToken.None));
    }

    [Fact]
    public async Task ArchivingAndRestoring_TogglesTheProductAndSaves()
    {
        var product = new Product("Widget", "desc", 9.99m, "Electronics");
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        var sut = new SetProductArchivedCommandHandler(_repo.Object, Mock.Of<ILogger<SetProductArchivedCommandHandler>>());

        await sut.Handle(new SetProductArchivedCommand(product.Id, true), CancellationToken.None);
        Assert.True(product.IsArchived);
        await sut.Handle(new SetProductArchivedCommand(product.Id, false), CancellationToken.None);
        Assert.False(product.IsArchived);

        _repo.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task GetProductByIdQuery_OfAnArchivedProduct_IsHiddenUnlessAskedFor()
    {
        var product = new Product("Widget", "desc", 9.99m, "Electronics");
        product.Archive();
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        var sut = new GetProductByIdQueryHandler(_repo.Object);

        Assert.Null(await sut.Handle(new GetProductByIdQuery(product.Id), CancellationToken.None));
        var admin = await sut.Handle(new GetProductByIdQuery(product.Id, IncludeArchived: true), CancellationToken.None);
        Assert.True(admin!.IsArchived);
    }
}
