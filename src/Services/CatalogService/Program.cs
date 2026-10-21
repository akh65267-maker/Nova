using System.Text;
using CatalogService;
using CatalogService.Application.Categories;
using CatalogService.Application.Products;
using CatalogService.Domain;
using CatalogService.Infrastructure.Persistence;
using CatalogService.Infrastructure.Storage;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using Observability;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability("catalog-service");

var connectionString = builder.Configuration.GetConnectionString("CatalogDatabase")
    ?? throw new InvalidOperationException("CatalogDatabase connection string is missing.");

builder.Services.AddDbContext<CatalogDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
        npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null)));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogDbContext>();

builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddSingleton<IProductImageStore, FileProductImageStore>();
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<Program>());

var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key configuration is missing.");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "TaskManager";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "TaskManager";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = true,
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/products", async (
    HttpRequest httpRequest,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var query = httpRequest.Query;

    var sort = query["sort"].ToString() switch
    {
        "price-asc" => ProductSortBy.PriceAscending,
        "price-desc" => ProductSortBy.PriceDescending,
        "name" => ProductSortBy.NameAscending,
        "best-offer" => ProductSortBy.BestOffer,
        _ => ProductSortBy.Newest,
    };

    var page = int.TryParse(query["page"], out var p) && p > 0 ? p : 1;
    var pageSize = int.TryParse(query["pageSize"], out var ps) && ps is > 0 and <= 100 ? ps : 20;

    var result = await mediator.Send(new ProductListQuery(
        Category: query["category"].ToString() is { Length: > 0 } category ? category : null,
        MinPrice: decimal.TryParse(query["minPrice"], out var minPrice) ? minPrice : null,
        MaxPrice: decimal.TryParse(query["maxPrice"], out var maxPrice) ? maxPrice : null,
        Search: query["search"].ToString() is { Length: > 0 } search ? search : null,
        Sort: sort,
        Page: page,
        PageSize: pageSize,
        Brand: query["brand"].ToString() is { Length: > 0 } brand ? brand : null,
        OnSale: query["onSale"] == "true",
        Ids: query["ids"].ToString() is { Length: > 0 } ids ? ParseIds(ids) : null,
        // Archived products are for the admin console only; anyone else asking gets the shop's list.
        IncludeArchived: query["includeArchived"] == "true" && httpRequest.HttpContext.User.IsInRole("Admin")), cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/categories", async (IMediator mediator, CancellationToken cancellationToken) =>
    Results.Ok(await mediator.Send(new ListCategoriesQuery(), cancellationToken)));

app.MapPost("/categories", async (
    CategoryRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var (outcome, category) = await mediator.Send(new CreateCategoryCommand(request.Name), cancellationToken);

    return outcome == CategoryOutcome.Done
        ? Results.Created($"/categories/{category!.Id}", category)
        : CategoryProblem(outcome);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/categories/{id:guid}", async (
    Guid id,
    CategoryRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var outcome = await mediator.Send(new RenameCategoryCommand(id, request.Name), cancellationToken);

    return outcome == CategoryOutcome.Done ? Results.NoContent() : CategoryProblem(outcome);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/categories/{id:guid}", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var outcome = await mediator.Send(new DeleteCategoryCommand(id), cancellationToken);

    return outcome == CategoryOutcome.Done ? Results.NoContent() : CategoryProblem(outcome);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// The ids in a comma-separated list; anything that is not an id is left out.
static IReadOnlyCollection<Guid> ParseIds(string list) =>
    list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => Guid.TryParse(x, out var id) ? id : (Guid?)null)
        .OfType<Guid>()
        .Take(100)
        .ToList();

static IResult CategoryProblem(CategoryOutcome outcome) => outcome switch
{
    CategoryOutcome.NotFound => Results.NotFound(),
    CategoryOutcome.NameTaken => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A category with this name already exists."),
    _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This category still has products. Move or delete them first."),
};

app.MapGet("/products/brands", async (IMediator mediator, CancellationToken cancellationToken) =>
    Results.Ok(await mediator.Send(new ListBrandsQuery(), cancellationToken)));

app.MapGet("/products/{id:guid}", async (
    Guid id,
    HttpRequest httpRequest,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var product = await mediator.Send(new GetProductByIdQuery(id, httpRequest.HttpContext.User.IsInRole("Admin")), cancellationToken);

    return product is null ? Results.NotFound() : Results.Ok(product);
});

app.MapGet("/products/{id:guid}/related", async (
    Guid id,
    int? limit,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var related = await mediator.Send(new RelatedProductsQuery(id, limit is > 0 and <= 12 ? limit.Value : 4), cancellationToken);

    return related is null ? Results.NotFound() : Results.Ok(related);
});

app.MapPost("/products", async (
    CreateProductRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var id = await mediator.Send(new CreateProductCommand(request.Name, request.Description, request.Price, request.Category, request.Brand, request.Attributes), cancellationToken);

    return Results.Created($"/products/{id}", new { id });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/products/{id:guid}", async (
    Guid id,
    CreateProductRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var found = await mediator.Send(new UpdateProductCommand(id, request.Name, request.Description, request.Price, request.Category, request.Brand, request.Attributes), cancellationToken);

    return found ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// Archiving takes a product off sale without deleting it: orders still refer to it.
app.MapPost("/products/{id:guid}/archive", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
    await mediator.Send(new SetProductArchivedCommand(id, Archived: true), cancellationToken) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPost("/products/{id:guid}/restore", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
    await mediator.Send(new SetProductArchivedCommand(id, Archived: false), cancellationToken) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

// The picture itself: public, like the product it belongs to. The URL in a product carries the
// picture's version, so a browser may keep it for good; a request without one revalidates.
app.MapGet("/products/{id:guid}/image", async (
    Guid id,
    string? v,
    HttpResponse response,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var image = await mediator.Send(new GetProductImageQuery(id), cancellationToken);
    if (image is null)
        return Results.NotFound();

    response.Headers.XContentTypeOptions = "nosniff";
    response.Headers.CacheControl = v == image.Version ? "public, max-age=31536000, immutable" : "public, no-cache";

    return Results.File(image.Content, image.ContentType, entityTag: new EntityTagHeaderValue($"\"{image.Version}\""));
});

// The body is the picture itself (image/jpeg, image/png or image/webp), not a form.
app.MapPut("/products/{id:guid}/image", async (
    Guid id,
    HttpRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var tooLarge = Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: $"A product picture can be at most {ProductImage.MaxBytes / (1024 * 1024)} MB.");

    if (request.ContentLength > ProductImage.MaxBytes)
        return tooLarge;

    // Read at most one byte more than allowed, so a body with no declared length cannot fill memory.
    var buffer = new byte[ProductImage.MaxBytes + 1];
    var length = 0;
    int read;
    while (length < buffer.Length
           && (read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
    {
        length += read;
    }

    if (length > ProductImage.MaxBytes)
        return tooLarge;

    var result = await mediator.Send(new SetProductImageCommand(id, buffer[..length]), cancellationToken);

    return result.Outcome switch
    {
        SetProductImageOutcome.Set => Results.Ok(new { imageUrl = result.ImageUrl }),
        SetProductImageOutcome.ProductNotFound => Results.NotFound(),
        _ => Results.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            title: "The picture must be a JPEG, PNG or WebP image."),
    };
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

// Extra pictures beside the main one. The body is the picture itself, as for the main picture.
app.MapGet("/products/{id:guid}/gallery/{version}", async (
    Guid id,
    string version,
    HttpResponse response,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var image = await mediator.Send(new GetGalleryImageQuery(id, version), cancellationToken);
    if (image is null)
        return Results.NotFound();

    response.Headers.XContentTypeOptions = "nosniff";
    response.Headers.CacheControl = "public, max-age=31536000, immutable";

    return Results.File(image.Content, image.ContentType, entityTag: new EntityTagHeaderValue($"\"{version}\""));
});

app.MapPost("/products/{id:guid}/gallery", async (
    Guid id,
    HttpRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var tooLarge = Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: $"A product picture can be at most {ProductImage.MaxBytes / (1024 * 1024)} MB.");

    if (request.ContentLength > ProductImage.MaxBytes)
        return tooLarge;

    var buffer = new byte[ProductImage.MaxBytes + 1];
    var length = 0;
    int read;
    while (length < buffer.Length
           && (read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
    {
        length += read;
    }

    if (length > ProductImage.MaxBytes)
        return tooLarge;

    var result = await mediator.Send(new AddGalleryImageCommand(id, buffer[..length]), cancellationToken);

    return result.Outcome switch
    {
        AddGalleryImageOutcome.Added => Results.Ok(new { url = result.Url }),
        AddGalleryImageOutcome.ProductNotFound => Results.NotFound(),
        AddGalleryImageOutcome.TooMany => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: $"A product can have at most {GalleryImage.MaxPerProduct} gallery pictures."),
        _ => Results.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            title: "The picture must be a JPEG, PNG or WebP image."),
    };
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/products/{id:guid}/gallery/{version}", async (
    Guid id,
    string version,
    IMediator mediator,
    CancellationToken cancellationToken) =>
    await mediator.Send(new RemoveGalleryImageCommand(id, version), cancellationToken) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapGet("/products/{id:guid}/discount", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var lookup = await mediator.Send(new GetDiscountQuery(id), cancellationToken);

    return !lookup.ProductFound ? Results.NotFound() : lookup.Discount is null ? Results.NoContent() : Results.Ok(lookup.Discount);
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/products/{id:guid}/discount", async (
    Guid id,
    DiscountDto request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
    await mediator.Send(new SetDiscountCommand(id, request.Kind, request.Value, request.StartsAtUtc, request.EndsAtUtc), cancellationToken)
        ? Results.NoContent()
        : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/products/{id:guid}/discount", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
    await mediator.Send(new ClearDiscountCommand(id), cancellationToken) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapDelete("/products/{id:guid}/image", async (
    Guid id,
    IMediator mediator,
    CancellationToken cancellationToken) =>
    await mediator.Send(new ClearProductImageCommand(id), cancellationToken) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization(policy => policy.RequireRole("Admin"));

app.UseExceptionHandler();

app.MapHealthChecks("/health");

app.UseHttpsRedirection();

app.Run();
