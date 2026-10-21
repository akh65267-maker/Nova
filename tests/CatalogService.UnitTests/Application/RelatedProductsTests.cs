using CatalogService.Application.Products;
using CatalogService.Domain;
using Moq;

namespace CatalogService.UnitTests.Application;

public class RelatedProductsTests
{
    private readonly Mock<IProductRepository> _products = new();

    [Fact]
    public async Task AskedForTheProductsOfItsBrandAndCategory_NotTheProductItself()
    {
        var product = new Product("Widget", "d", 1m, "Electronics", "Acme");
        var other = new Product("Gadget", "d", 2m, "Electronics", "Acme");
        _products.Setup(x => x.GetByIdAsync(product.Id, default)).ReturnsAsync(product);
        _products.Setup(x => x.ListRelatedAsync(product.Id, "Electronics", "Acme", 4, default)).ReturnsAsync(new[] { other });

        var result = await new RelatedProductsQueryHandler(_products.Object).Handle(new RelatedProductsQuery(product.Id), default);

        Assert.Equal("Gadget", Assert.Single(result!).Name);
    }

    [Fact]
    public async Task UnknownProduct_GivesNull()
    {
        var result = await new RelatedProductsQueryHandler(_products.Object).Handle(new RelatedProductsQuery(Guid.NewGuid()), default);

        Assert.Null(result);
    }

    [Fact]
    public async Task ArchivedProduct_GivesNull()
    {
        var product = new Product("Widget", "d", 1m, "Electronics");
        product.Archive();
        _products.Setup(x => x.GetByIdAsync(product.Id, default)).ReturnsAsync(product);

        Assert.Null(await new RelatedProductsQueryHandler(_products.Object).Handle(new RelatedProductsQuery(product.Id), default));
    }
}
