using CatalogService.Domain;

namespace CatalogService.UnitTests.Domain;

public class ProductGalleryTests
{
    private static Product Widget() => new("Widget", "d", 1m, "Electronics");

    [Fact]
    public void Gallery_IsEmptyByDefault()
    {
        Assert.Empty(Widget().Gallery);
    }

    [Fact]
    public void AddGalleryImage_AddsOnce()
    {
        var product = Widget();

        Assert.True(product.AddGalleryImage("image/png", "aa"));
        Assert.False(product.AddGalleryImage("image/png", "aa"));
        Assert.Single(product.Gallery);
    }

    [Fact]
    public void AddGalleryImage_RefusesTheMainPicture()
    {
        var product = Widget();
        product.SetImage("image/png", "aa");

        Assert.False(product.AddGalleryImage("image/png", "aa"));
        Assert.Empty(product.Gallery);
    }

    [Fact]
    public void AddGalleryImage_IsLimited()
    {
        var product = Widget();
        for (var i = 0; i < GalleryImage.MaxPerProduct; i++)
            product.AddGalleryImage("image/png", $"a{i}");

        Assert.Throws<InvalidOperationException>(() => product.AddGalleryImage("image/png", "extra"));
    }

    [Fact]
    public void RemoveGalleryImage_RemovesOnlyThatOne()
    {
        var product = Widget();
        product.AddGalleryImage("image/png", "aa");
        product.AddGalleryImage("image/png", "bb");

        Assert.True(product.RemoveGalleryImage("aa"));
        Assert.False(product.RemoveGalleryImage("aa"));
        Assert.Equal("bb", Assert.Single(product.Gallery).Version);
    }

    [Fact]
    public void UsesPicture_CoversMainAndGallery()
    {
        var product = Widget();
        product.SetImage("image/png", "aa");
        product.AddGalleryImage("image/png", "bb");

        Assert.True(product.UsesPicture("aa"));
        Assert.True(product.UsesPicture("bb"));
        Assert.False(product.UsesPicture("cc"));
    }
}
