namespace CatalogService.Application.Products;

/// <summary>Where the picture files live. A file is named by the product and the picture's version, nothing a caller sends.</summary>
public interface IProductImageStore
{
    Task SaveAsync(Guid productId, string version, byte[] content, CancellationToken cancellationToken = default);
    Stream? OpenRead(Guid productId, string version);
    void Delete(Guid productId, string version);
}
