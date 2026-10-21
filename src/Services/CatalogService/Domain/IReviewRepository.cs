namespace CatalogService.Domain;

/// <param name="Breakdown">How many reviews gave 1, 2, 3, 4 and 5 stars, in that order.</param>
public sealed record RatingSummary(int Count, int Total, int[] Breakdown);

public interface IReviewRepository
{
    Task<Review?> GetAsync(Guid productId, Guid userId, CancellationToken cancellationToken = default);
    Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<Review>> ListAsync(Guid productId, int limit, CancellationToken cancellationToken = default);
    Task<RatingSummary> SummarizeAsync(Guid productId, CancellationToken cancellationToken = default);
    Task AddAsync(Review review, CancellationToken cancellationToken = default);
    void Remove(Review review);
}
