namespace CatalogService.Domain;

/// <summary>An extra picture of a product, beside its main one. Named by its version, like the main picture.</summary>
public sealed record GalleryImage(string Version, string ContentType)
{
    public const int MaxPerProduct = 8;
}
