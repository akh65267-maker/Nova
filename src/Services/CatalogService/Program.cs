using System.Text;
using CatalogService;
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
        // Archived products are for the admin console only; anyone else asking gets the shop's list.
        IncludeArchived: query["includeArchived"] == "true" && httpRequest.HttpContext.User.IsInRole("Admin")), cancellationToken);

    return Results.Ok(result);
});

app.MapGet("/products/{id:guid}", async (
    Guid id,
    HttpRequest httpRequest,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var product = await mediator.Send(new GetProductByIdQuery(id, httpRequest.HttpContext.User.IsInRole("Admin")), cancellationToken);

    return product is null ? Results.NotFound() : Results.Ok(product);
});

app.MapPost("/products", async (
    CreateProductRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var id = await mediator.Send(new CreateProductCommand(request.Name, request.Description, request.Price, request.Category), cancellationToken);

    return Results.Created($"/products/{id}", new { id });
}).RequireAuthorization(policy => policy.RequireRole("Admin"));

app.MapPut("/products/{id:guid}", async (
    Guid id,
    CreateProductRequest request,
    IMediator mediator,
    CancellationToken cancellationToken) =>
{
    var found = await mediator.Send(new UpdateProductCommand(id, request.Name, request.Description, request.Price, request.Category), cancellationToken);

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
