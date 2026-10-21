using CatalogService.Domain;

namespace CatalogService.UnitTests.Domain;

public class ProductBrandTests
{
    [Fact]
    public void Brand_IsOptional()
    {
        Assert.Null(new Product("Widget", "d", 1m, "Electronics").Brand);
    }

    [Theory]
    [InlineData("  Acme ", "Acme")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    public void Brand_IsTrimmedAndBlankMeansNone(string given, string? expected)
    {
        Assert.Equal(expected, new Product("Widget", "d", 1m, "Electronics", given).Brand);
    }

    [Fact]
    public void Update_CanClearTheBrand()
    {
        var product = new Product("Widget", "d", 1m, "Electronics", "Acme");

        product.Update("Widget", "d", 1m, "Electronics", null);

        Assert.Null(product.Brand);
    }

    [Fact]
    public void Brand_TooLong_IsRefused()
    {
        Assert.Throws<ArgumentException>(
            () => new Product("Widget", "d", 1m, "Electronics", new string('a', Product.MaxBrandLength + 1)));
    }
}
