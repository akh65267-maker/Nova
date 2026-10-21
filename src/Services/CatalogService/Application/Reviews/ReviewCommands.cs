using CatalogService.Domain;
using MediatR;

namespace CatalogService.Application.Reviews;

public sealed record ReviewDto(Guid Id, string AuthorName, int Rating, string Body, DateTimeOffset CreatedAtUtc, bool IsMine);

/// <param name="Average">Null while there are no reviews.</param>
/// <param name="Breakdown">How many reviews gave 1, 2, 3, 4 and 5 stars, in that order.</param>
public sealed record RatingSummaryDto(double? Average, int Count, int[] Breakdown);

/// <param name="Mine">The caller's own review, when they have written one.</param>
public sealed record ProductReviewsDto(RatingSummaryDto Summary, IReadOnlyCollection<ReviewDto> Items, ReviewDto? Mine);

/// <summary>Null when the product does not exist or is off sale.</summary>
public sealed record ListReviewsQuery(Guid ProductId, Guid? CallerId) : IRequest<ProductReviewsDto?>;

/// <summary>Writes the caller's review of a product, replacing the one they had. Returns false when there is no such product.</summary>
public sealed record SaveReviewCommand(Guid ProductId, Guid UserId, string Email, int Rating, string? Body) : IRequest<bool>;

/// <summary>Removes the caller's own review. Returns false only when there is no such product.</summary>
public sealed record DeleteOwnReviewCommand(Guid ProductId, Guid UserId) : IRequest<bool>;

/// <summary>Moderation: removes any review. Returns false when there is no such review.</summary>
public sealed record DeleteReviewCommand(Guid ProductId, Guid ReviewId) : IRequest<bool>;

internal static class ReviewMapping
{
    public const int PageSize = 50;

    public static ReviewDto ToDto(Review r, Guid? callerId) =>
        new(r.Id, r.AuthorName, r.Rating, r.Body, r.CreatedAtUtc, callerId is { } id && id == r.UserId);

    public static async Task RefreshRatingAsync(Product product, IReviewRepository reviews, CancellationToken cancellationToken)
    {
        var summary = await reviews.SummarizeAsync(product.Id, cancellationToken);
        product.SetRating(summary.Count, summary.Total);
    }
}

public sealed class ListReviewsQueryHandler : IRequestHandler<ListReviewsQuery, ProductReviewsDto?>
{
    private readonly IProductRepository _products;
    private readonly IReviewRepository _reviews;

    public ListReviewsQueryHandler(IProductRepository products, IReviewRepository reviews)
    {
        _products = products;
        _reviews = reviews;
    }

    public async Task<ProductReviewsDto?> Handle(ListReviewsQuery request, CancellationToken cancellationToken)
    {
        var product = await _products.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null || product.IsArchived)
            return null;

        var summary = await _reviews.SummarizeAsync(product.Id, cancellationToken);
        var items = (await _reviews.ListAsync(product.Id, ReviewMapping.PageSize, cancellationToken))
            .Select(r => ReviewMapping.ToDto(r, request.CallerId))
            .ToList();

        // The caller's own review may be older than the page of newest ones.
        ReviewDto? mine = null;
        if (request.CallerId is { } callerId)
        {
            var own = await _reviews.GetAsync(product.Id, callerId, cancellationToken);
            mine = own is null ? null : ReviewMapping.ToDto(own, callerId);
        }

        return new ProductReviewsDto(
            new RatingSummaryDto(summary.Count == 0 ? null : Math.Round((double)summary.Total / summary.Count, 1), summary.Count, summary.Breakdown),
            items,
            mine);
    }
}

public sealed class SaveReviewCommandHandler : IRequestHandler<SaveReviewCommand, bool>
{
    private readonly IProductRepository _products;
    private readonly IReviewRepository _reviews;

    public SaveReviewCommandHandler(IProductRepository products, IReviewRepository reviews)
    {
        _products = products;
        _reviews = reviews;
    }

    public async Task<bool> Handle(SaveReviewCommand request, CancellationToken cancellationToken)
    {
        var product = await _products.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null || product.IsArchived)
            return false;

        var review = await _reviews.GetAsync(product.Id, request.UserId, cancellationToken);
        if (review is null)
            await _reviews.AddAsync(new Review(product.Id, request.UserId, request.Email, request.Rating, request.Body), cancellationToken);
        else
            review.Edit(request.Rating, request.Body);

        // The new row is not in the database yet: save it, then count.
        await _products.SaveChangesAsync(cancellationToken);
        await ReviewMapping.RefreshRatingAsync(product, _reviews, cancellationToken);
        await _products.SaveChangesAsync(cancellationToken);

        return true;
    }
}

public sealed class DeleteOwnReviewCommandHandler : IRequestHandler<DeleteOwnReviewCommand, bool>
{
    private readonly IProductRepository _products;
    private readonly IReviewRepository _reviews;

    public DeleteOwnReviewCommandHandler(IProductRepository products, IReviewRepository reviews)
    {
        _products = products;
        _reviews = reviews;
    }

    public async Task<bool> Handle(DeleteOwnReviewCommand request, CancellationToken cancellationToken)
    {
        var product = await _products.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        var review = await _reviews.GetAsync(product.Id, request.UserId, cancellationToken);
        if (review is not null)
        {
            _reviews.Remove(review);
            await _products.SaveChangesAsync(cancellationToken);
            await ReviewMapping.RefreshRatingAsync(product, _reviews, cancellationToken);
            await _products.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

public sealed class DeleteReviewCommandHandler : IRequestHandler<DeleteReviewCommand, bool>
{
    private readonly IProductRepository _products;
    private readonly IReviewRepository _reviews;

    public DeleteReviewCommandHandler(IProductRepository products, IReviewRepository reviews)
    {
        _products = products;
        _reviews = reviews;
    }

    public async Task<bool> Handle(DeleteReviewCommand request, CancellationToken cancellationToken)
    {
        var review = await _reviews.GetByIdAsync(request.ReviewId, cancellationToken);
        if (review is null || review.ProductId != request.ProductId)
            return false;

        var product = await _products.GetByIdAsync(review.ProductId, cancellationToken);

        _reviews.Remove(review);
        await _products.SaveChangesAsync(cancellationToken);

        if (product is not null)
        {
            await ReviewMapping.RefreshRatingAsync(product, _reviews, cancellationToken);
            await _products.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
