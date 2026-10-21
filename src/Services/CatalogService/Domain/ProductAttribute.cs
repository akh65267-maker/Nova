namespace CatalogService.Domain;

/// <summary>A named fact about a product, such as Colour: Black.</summary>
public sealed record ProductAttribute(string Name, string Value)
{
    public const int MaxNameLength = 50;
    public const int MaxValueLength = 200;
    public const int MaxPerProduct = 20;
}
