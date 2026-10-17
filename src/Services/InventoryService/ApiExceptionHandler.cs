using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Observability;

namespace InventoryService;

public class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A request the framework could not read (malformed JSON, a query value of the wrong
        // type) is the caller's mistake, not a server fault.
        if (exception is BadHttpRequestException badRequest)
        {
            var status = BadRequestHandling.StatusFor(badRequest);
            httpContext.Response.StatusCode = status;

            await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status,
                Title = BadRequestHandling.Title,
                Detail = BadRequestHandling.Detail
            }, cancellationToken);

            return true;
        }

        if (exception is not ArgumentException argumentException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid request.",
            Detail = argumentException.Message
        }, cancellationToken);

        return true;
    }
}
