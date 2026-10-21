using CatalogService.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Persistence;

public class ReviewRepository : IReviewRepository
{
    private readonly CatalogDbContext _db;

    public ReviewRepository(CatalogDbContext db)
    {
        _db = db;
    }

    public Task<Review?> GetAsync(Guid productId, Guid userId, CancellationToken cancellationToken = default) =>
        _db.Reviews.FirstOrDefaultAsync(x => x.ProductId == productId && x.UserId == userId, cancellationToken);

    public Task<Review?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _db.Reviews.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyCollection<Review>> ListAsync(Guid productId, int limit, CancellationToken cancellationToken = default) =>
        await _db.Reviews
            .AsNoTracking()
            .Where(x => x.ProductId == productId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<RatingSummary> SummarizeAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var counts = await _db.Reviews
            .Where(x => x.ProductId == productId)
            .GroupBy(x => x.Rating)
            .Select(g => new { Rating = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var breakdown = new int[5];
        foreach (var c in counts)
            breakdown[c.Rating - 1] = c.Count;

        return new RatingSummary(breakdown.Sum(), breakdown.Select((n, i) => n * (i + 1)).Sum(), breakdown);
    }

    public async Task AddAsync(Review review, CancellationToken cancellationToken = default) =>
        await _db.Reviews.AddAsync(review, cancellationToken);

    public void Remove(Review review) => _db.Reviews.Remove(review);
}
