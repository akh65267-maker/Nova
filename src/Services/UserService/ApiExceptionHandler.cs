using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Observability;
using UserService.Application;

namespace UserService;

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
            var badRequestStatus = BadRequestHandling.StatusFor(badRequest);
            httpContext.Response.StatusCode = badRequestStatus;

            await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = badRequestStatus,
                Title = BadRequestHandling.Title,
                Detail = BadRequestHandling.Detail
            }, cancellationToken);

            return true;
        }

        var (status, title) = exception switch
        {
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request."),
            InvalidCredentialsException => (StatusCodes.Status401Unauthorized, "Authentication failed."),
            InvalidRefreshTokenException => (StatusCodes.Status401Unauthorized, "Authentication failed."),
            _ => (0, string.Empty)
        };

        if (status == 0)
            return false;

        httpContext.Response.StatusCode = status;

        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message
        }, cancellationToken);

        return true;
    }
}
