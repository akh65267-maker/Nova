using MediatR;

namespace CatalogService.Application.Products;

public enum SetProductImageOutcome { Set, ProductNotFound, NotAnImage }

public sealed record SetProductImageResult(SetProductImageOutcome Outcome, string? ImageUrl = null);

public sealed record SetProductImageCommand(Guid ProductId, byte[] Content) : IRequest<SetProductImageResult>;
