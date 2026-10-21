namespace CatalogService.Application.Reviews;

public sealed record ReviewRequest(int Rating, string? Body = null);
