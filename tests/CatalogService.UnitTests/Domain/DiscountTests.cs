using CatalogService.Application.Products;
using CatalogService.Domain;

namespace CatalogService.UnitTests.Domain;

public class DiscountTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Product Widget(decimal price = 100m) => new("Widget", "desc", price, "Electronics");

    [Fact]
    public void WithoutADiscount_TheEffectivePriceIsThePrice()
    {
        Assert.Equal(100m, Widget().EffectivePrice(Now));
    }

    [Fact]
    public void PercentDiscount_TakesThePercentOff_RoundedToCents()
    {
        var product = Widget(19.99m);
        product.SetDiscount(new Discount(DiscountKind.Percent, 15));

        Assert.Equal(16.99m, product.EffectivePrice(Now));
    }

    [Fact]
    public void FixedDiscount_TakesTheAmountOff()
    {
        var product = Widget();
        product.SetDiscount(new Discount(DiscountKind.Fixed, 30m));

        Assert.Equal(70m, product.EffectivePrice(Now));
    }

    [Fact]
    public void Discount_AppliesOnlyInsideItsDates()
    {
        var product = Widget();
        product.SetDiscount(new Discount(DiscountKind.Percent, 50, Now.AddDays(1), Now.AddDays(2)));

        Assert.Equal(100m, product.EffectivePrice(Now));
        Assert.Equal(50m, product.EffectivePrice(Now.AddDays(1)));
        Assert.Equal(100m, product.EffectivePrice(Now.AddDays(2)));
    }

    [Fact]
    public void FixedDiscount_NotBelowThePriceAnyMore_IsIgnored()
    {
        var product = Widget();
        product.SetDiscount(new Discount(DiscountKind.Fixed, 60m));
        product.Update("Widget", "desc", 50m, "Electronics");

        Assert.Equal(50m, product.EffectivePrice(Now));
    }

    [Theory]
    [InlineData(DiscountKind.Percent, 0)]
    [InlineData(DiscountKind.Percent, 100)]
    [InlineData(DiscountKind.Percent, -5)]
    [InlineData(DiscountKind.Fixed, 0)]
    [InlineData(DiscountKind.Fixed, 100)]
    public void SetDiscount_RejectsAnAmountOutOfRange(DiscountKind kind, decimal value)
    {
        Assert.Throws<ArgumentException>(() => Widget().SetDiscount(new Discount(kind, value)));
    }

    [Fact]
    public void SetDiscount_RejectsAnEndBeforeTheStart()
    {
        Assert.Throws<ArgumentException>(() => Widget().SetDiscount(new Discount(DiscountKind.Percent, 10, Now, Now)));
    }

    [Fact]
    public void ClearDiscount_RestoresThePrice()
    {
        var product = Widget();
        product.SetDiscount(new Discount(DiscountKind.Percent, 10));
        product.ClearDiscount();

        Assert.Equal(100m, product.EffectivePrice(Now));
    }

    [Fact]
    public void Dto_ShowsTheSalePriceTheListPriceAndThePercent_OnlyWhileActive()
    {
        var product = Widget();
        product.SetDiscount(new Discount(DiscountKind.Fixed, 25m, null, Now.AddDays(1)));

        var on = ProductDto.From(product, Now);
        var off = ProductDto.From(product, Now.AddDays(2));

        Assert.Equal((75m, 100m, 25, Now.AddDays(1)), (on.Price, on.ListPrice, on.DiscountPercent, on.DiscountEndsAtUtc));
        Assert.Equal((100m, 100m, (int?)null, (DateTimeOffset?)null), (off.Price, off.ListPrice, off.DiscountPercent, off.DiscountEndsAtUtc));
    }
}
