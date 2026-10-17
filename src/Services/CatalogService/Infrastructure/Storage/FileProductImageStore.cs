using CatalogService.Application.Products;

namespace CatalogService.Infrastructure.Storage;

/// <summary>
/// Keeps each picture as one file in a directory (a Docker volume in the compose setup). The file
/// name is built only from the product's Guid and a hex version, so nothing a caller sends can
/// reach another path.
/// </summary>
public sealed class FileProductImageStore : IProductImageStore
{
    private readonly string _directory;

    public FileProductImageStore(IConfiguration configuration)
    {
        _directory = Path.GetFullPath(configuration["ProductImages:Directory"] ?? "product-images");
        Directory.CreateDirectory(_directory);
    }

    public async Task SaveAsync(Guid productId, string version, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = PathFor(productId, version);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public Stream? OpenRead(Guid productId, string version)
    {
        var path = PathFor(productId, version);

        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void Delete(Guid productId, string version)
    {
        var path = PathFor(productId, version);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(Guid productId, string version)
    {
        if (version.Length == 0 || !version.All(Uri.IsHexDigit))
            throw new ArgumentException("A picture version is hexadecimal.", nameof(version));

        return Path.Combine(_directory, $"{productId:N}-{version}");
    }
}
