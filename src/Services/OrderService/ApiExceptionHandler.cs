using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Orders;

namespace OrderService;

public class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OrderRejectedException rejected)
        {
            httpContext.Response.StatusCode = rejected.StatusCode;

            await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = rejected.StatusCode,
                Title = rejected.Title,
                Detail = rejected.Message
            }, cancellationToken);

            return true;
        }

        // A query value that is not the right type (an unparseable date) is the caller's mistake.
        if (exception is BadHttpRequestException badRequest)
        {
            httpContext.Response.StatusCode = badRequest.StatusCode;

            await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "Invalid request.",
                Detail = badRequest.Message
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
