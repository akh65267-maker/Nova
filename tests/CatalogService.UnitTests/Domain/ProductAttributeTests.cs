using CatalogService.Domain;

namespace CatalogService.UnitTests.Domain;

public class ProductAttributeTests
{
    private static Product Widget(params ProductAttribute[] attributes) =>
        new("Widget", "d", 1m, "Electronics", null, attributes);

    [Fact]
    public void Attributes_AreEmptyByDefault()
    {
        Assert.Empty(new Product("Widget", "d", 1m, "Electronics").Attributes);
    }

    [Fact]
    public void Attributes_AreTrimmedAndBlankRowsDropped()
    {
        var product = Widget(new ProductAttribute(" Colour ", " Black "), new ProductAttribute("", " "));

        Assert.Equal(new ProductAttribute("Colour", "Black"), Assert.Single(product.Attributes));
    }

    [Fact]
    public void Attributes_RepeatedNameKeepsLastValue()
    {
        var product = Widget(new ProductAttribute("Colour", "Black"), new ProductAttribute("colour", "White"));

        Assert.Equal("White", Assert.Single(product.Attributes).Value);
    }

    [Fact]
    public void Update_WithNullLeavesAttributes_AndWithListReplacesThem()
    {
        var product = Widget(new ProductAttribute("Colour", "Black"));

        product.Update("Widget", "d", 1m, "Electronics");
        Assert.Single(product.Attributes);

        product.Update("Widget", "d", 1m, "Electronics", null, new[] { new ProductAttribute("Size", "L") });
        Assert.Equal("Size", Assert.Single(product.Attributes).Name);

        product.Update("Widget", "d", 1m, "Electronics", null, Array.Empty<ProductAttribute>());
        Assert.Empty(product.Attributes);
    }

    [Fact]
    public void Attribute_NeedsBothNameAndValue()
    {
        Assert.Throws<ArgumentException>(() => Widget(new ProductAttribute("Colour", " ")));
        Assert.Throws<ArgumentException>(() => Widget(new ProductAttribute(" ", "Black")));
    }

    [Fact]
    public void Attributes_AreLimited()
    {
        Assert.Throws<ArgumentException>(() => Widget(new ProductAttribute(new string('n', ProductAttribute.MaxNameLength + 1), "v")));
        Assert.Throws<ArgumentException>(() => Widget(new ProductAttribute("n", new string('v', ProductAttribute.MaxValueLength + 1))));
        Assert.Throws<ArgumentException>(() =>
            Widget(Enumerable.Range(0, ProductAttribute.MaxPerProduct + 1).Select(i => new ProductAttribute($"n{i}", "v")).ToArray()));
    }
}
