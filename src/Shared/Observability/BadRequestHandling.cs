using Microsoft.AspNetCore.Http;

namespace Observability;

/// <summary>
/// How a service answers a request the framework itself could not read: a body that is not valid
/// JSON for the endpoint, a query value of the wrong type. ASP.NET Core throws
/// <see cref="BadHttpRequestException"/> for these, and a handler that does not recognise it turns
/// it into a 500 and an error in the log for what is the caller's mistake. Shared so the five
/// services' exception handlers answer identically.
/// </summary>
public static class BadRequestHandling
{
    public const string Title = "Invalid request.";

    /// <summary>
    /// Fixed on purpose. The framework's own message names the endpoint's internal parameter and
    /// type ("Failed to read parameter \"LoginRequest request\" from the request body as JSON")
    /// and, for a query value, quotes what the caller sent.
    /// </summary>
    public const string Detail = "The request could not be read. Check its body and query string.";

    /// <summary>
    /// The framework's own client-error code (413 for a body that is too large, 408 for one that
    /// arrived too slowly, usually 400), and 400 for anything that is not a client error, so a
    /// "bad request" can never present itself as a success or as a server fault.
    /// </summary>
    public static int StatusFor(BadHttpRequestException exception) =>
        exception.StatusCode is >= 400 and < 500 ? exception.StatusCode : StatusCodes.Status400BadRequest;
}
