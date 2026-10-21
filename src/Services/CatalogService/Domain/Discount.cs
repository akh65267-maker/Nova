namespace CatalogService.Domain;

public enum DiscountKind { Percent, Fixed }

/// <summary>A reduction of a product's price, for as long as its dates allow. A product has one: discounts do not stack.</summary>
public sealed record Discount(DiscountKind Kind, decimal Value, DateTimeOffset? StartsAtUtc = null, DateTimeOffset? EndsAtUtc = null)
{
    public bool IsActiveAt(DateTimeOffset now) =>
        (StartsAtUtc is null || StartsAtUtc <= now) && (EndsAtUtc is null || now < EndsAtUtc);

    /// <summary>The price after the reduction. A fixed amount that is not below the price is ignored.</summary>
    public decimal Apply(decimal price) => Kind switch
    {
        DiscountKind.Percent => Math.Round(price * (100 - Value) / 100, 2, MidpointRounding.AwayFromZero),
        _ => Value < price ? price - Value : price,
    };
}
