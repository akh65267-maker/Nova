namespace CatalogService.Domain;

/// <summary>A customer's rating of a product, with an optional comment. One per customer per product.</summary>
public class Review
{
    public const int MaxBodyLength = 2000;
    public const int MaxAuthorLength = 20;

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid UserId { get; private set; }
    /// <summary>What other customers see: never the email, only its first letter.</summary>
    public string AuthorName { get; private set; }
    public int Rating { get; private set; }
    public string Body { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    private Review()
    {
    }

    public Review(Guid productId, Guid userId, string email, int rating, string? body)
    {
        Id = Guid.NewGuid();
        ProductId = productId;
        UserId = userId;
        AuthorName = AuthorNameFor(email);
        CreatedAtUtc = DateTimeOffset.UtcNow;
        Edit(rating, body);
    }

    public void Edit(int rating, string? body)
    {
        if (rating is < 1 or > 5)
            throw new ArgumentException("A rating is from 1 to 5.", nameof(rating));

        var trimmed = body?.Trim() ?? string.Empty;
        if (trimmed.Length > MaxBodyLength)
            throw new ArgumentException($"A review can be at most {MaxBodyLength} characters.", nameof(body));

        Rating = rating;
        Body = trimmed;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public static string AuthorNameFor(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "Customer" : char.ToUpperInvariant(email.Trim()[0]) + "***";
}
