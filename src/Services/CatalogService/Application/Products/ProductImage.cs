using System.Security.Cryptography;

namespace CatalogService.Application.Products;

/// <summary>
/// What counts as a product picture. The type is decided from the bytes, never from the file
/// name or the Content-Type the client sent, and only formats a browser shows without running
/// anything (no SVG, which can carry script) are accepted.
/// </summary>
public static class ProductImage
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
            return "image/jpeg";

        if (bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";

        // WebP: "RIFF", four size bytes, "WEBP".
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }

    /// <summary>Names one exact picture: it changes when the picture does, so a URL carrying it can be cached for good.</summary>
    public static string VersionOf(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    public static string GalleryUrlFor(Guid productId, string version) => $"/products/{productId}/gallery/{version}";

    public static string UrlFor(Guid productId, string version) => $"/products/{productId}/image?v={version}";
}
