using CatalogService.Application.Products;
using CatalogService.Domain;
using CatalogService.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CatalogService.UnitTests.Application;

public class ProductImageTests : IDisposable
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    private static readonly byte[] Webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();

    private readonly Mock<IProductRepository> _repo = new();
    private readonly Mock<IProductImageStore> _store = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "product-images-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static Product AProduct() => new("Widget", "desc", 9.99m, "Electronics");

    private SetProductImageCommandHandler SetHandler() =>
        new(_repo.Object, _store.Object, Mock.Of<ILogger<SetProductImageCommandHandler>>());

    // ---- what counts as a picture ----

    [Fact]
    public void DetectContentType_RecognisesJpegPngAndWebpByTheirBytes()
    {
        Assert.Equal("image/jpeg", ProductImage.DetectContentType(Jpeg));
        Assert.Equal("image/png", ProductImage.DetectContentType(Png));
        Assert.Equal("image/webp", ProductImage.DetectContentType(Webp));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("GIF89a......")]
    [InlineData("RIFF\0\0\0\0WAVEfmt ")]
    [InlineData("just some text")]
    [InlineData("")]
    public void DetectContentType_RefusesAnythingElse(string content)
    {
        Assert.Null(ProductImage.DetectContentType(System.Text.Encoding.Latin1.GetBytes(content)));
    }

    [Fact]
    public void VersionOf_ChangesWithTheBytesAndOnlyThen()
    {
        Assert.Equal(ProductImage.VersionOf(Jpeg), ProductImage.VersionOf((byte[])Jpeg.Clone()));
        Assert.NotEqual(ProductImage.VersionOf(Jpeg), ProductImage.VersionOf(Png));
        Assert.Equal(16, ProductImage.VersionOf(Jpeg).Length);
    }

    // ---- setting a picture ----

    [Fact]
    public async Task Set_WithSomethingThatIsNotAPicture_TouchesNothing()
    {
        var result = await SetHandler().Handle(new SetProductImageCommand(Guid.NewGuid(), "<svg/>"u8.ToArray()), CancellationToken.None);

        Assert.Equal(SetProductImageOutcome.NotAnImage, result.Outcome);
        _repo.VerifyNoOtherCalls();
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_WithAPictureOverTheLimit_IsRefused()
    {
        var tooBig = new byte[ProductImage.MaxBytes + 1];
        Jpeg.CopyTo(tooBig, 0);

        var result = await SetHandler().Handle(new SetProductImageCommand(Guid.NewGuid(), tooBig), CancellationToken.None);

        Assert.Equal(SetProductImageOutcome.NotAnImage, result.Outcome);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_ForAProductThatDoesNotExist_StoresNothing()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Product?)null);

        var result = await SetHandler().Handle(new SetProductImageCommand(Guid.NewGuid(), Jpeg), CancellationToken.None);

        Assert.Equal(SetProductImageOutcome.ProductNotFound, result.Outcome);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Set_StoresTheFileBeforeSavingTheRow_AndReturnsTheVersionedUrl()
    {
        var product = AProduct();
        var calls = new List<string>();
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);
        _repo.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("save row")).Returns(Task.CompletedTask);
        _store.Setup(x => x.SaveAsync(product.Id, It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("save file")).Returns(Task.CompletedTask);

        var result = await SetHandler().Handle(new SetProductImageCommand(product.Id, Png), CancellationToken.None);

        var version = ProductImage.VersionOf(Png);
        Assert.Equal(SetProductImageOutcome.Set, result.Outcome);
        Assert.Equal($"/products/{product.Id}/image?v={version}", result.ImageUrl);
        Assert.Equal(["save file", "save row"], calls);
        Assert.Equal("image/png", product.ImageContentType);
        Assert.Equal(version, product.ImageVersion);
    }

    [Fact]
    public async Task Set_ReplacingAPicture_DeletesTheOldFile()
    {
        var product = AProduct();
        product.SetImage("image/jpeg", ProductImage.VersionOf(Jpeg));
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        await SetHandler().Handle(new SetProductImageCommand(product.Id, Png), CancellationToken.None);

        _store.Verify(x => x.Delete(product.Id, ProductImage.VersionOf(Jpeg)), Times.Once);
    }

    [Fact]
    public async Task Set_WithTheSamePictureAgain_KeepsTheFile()
    {
        var product = AProduct();
        product.SetImage("image/jpeg", ProductImage.VersionOf(Jpeg));
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        await SetHandler().Handle(new SetProductImageCommand(product.Id, Jpeg), CancellationToken.None);

        _store.Verify(x => x.Delete(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // ---- removing and reading ----

    [Fact]
    public async Task Clear_RemovesTheRowsPictureAndTheFile()
    {
        var product = AProduct();
        product.SetImage("image/jpeg", "abc123");
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var found = await new ClearProductImageCommandHandler(_repo.Object, _store.Object)
            .Handle(new ClearProductImageCommand(product.Id), CancellationToken.None);

        Assert.True(found);
        Assert.Null(product.ImageVersion);
        _store.Verify(x => x.Delete(product.Id, "abc123"), Times.Once);
    }

    [Fact]
    public async Task Clear_WhenThereIsNoPicture_SucceedsAndTouchesNoFile()
    {
        var product = AProduct();
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var found = await new ClearProductImageCommandHandler(_repo.Object, _store.Object)
            .Handle(new ClearProductImageCommand(product.Id), CancellationToken.None);

        Assert.True(found);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Clear_ForAProductThatDoesNotExist_ReportsNotFound()
    {
        _repo.Setup(x => x.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Product?)null);

        var found = await new ClearProductImageCommandHandler(_repo.Object, _store.Object)
            .Handle(new ClearProductImageCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.False(found);
    }

    [Fact]
    public async Task Get_ForAProductWithoutAPicture_ReturnsNull()
    {
        var product = AProduct();
        _repo.Setup(x => x.GetByIdAsync(product.Id, It.IsAny<CancellationToken>())).ReturnsAsync(product);

        var result = await new GetProductImageQueryHandler(_repo.Object, _store.Object)
            .Handle(new GetProductImageQuery(product.Id), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void ProductDto_CarriesTheVersionedUrlOnlyWhenThereIsAPicture()
    {
        var product = AProduct();
        Assert.Null(ProductDto.From(product).ImageUrl);

        product.SetImage("image/png", "abc123");

        Assert.Equal($"/products/{product.Id}/image?v=abc123", ProductDto.From(product).ImageUrl);
    }

    // ---- the files ----

    private FileProductImageStore RealStore() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ProductImages:Directory"] = _directory }).Build());

    [Fact]
    public async Task FileStore_SavesReadsReplacesAndDeletes()
    {
        var store = RealStore();
        var id = Guid.NewGuid();

        await store.SaveAsync(id, "aa11", Jpeg);
        await using (var stream = store.OpenRead(id, "aa11")!)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(Jpeg, copy.ToArray());
        }

        await store.SaveAsync(id, "aa11", Png);
        await using (var stream = store.OpenRead(id, "aa11")!)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(Png, copy.ToArray());
        }

        store.Delete(id, "aa11");
        Assert.Null(store.OpenRead(id, "aa11"));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("zz")]
    public async Task FileStore_RefusesAVersionThatIsNotHexadecimal(string version)
    {
        var store = RealStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Guid.NewGuid(), version, Jpeg));
        Assert.Throws<ArgumentException>(() => store.OpenRead(Guid.NewGuid(), version));
        Assert.Empty(Directory.GetFiles(_directory));
    }
}
