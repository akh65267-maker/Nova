using System.Text.Json;
using Microsoft.AspNetCore.Http;
using UserService;

namespace UserService.UnitTests;

public class ApiExceptionHandlerTests
{
    private static async Task<(DefaultHttpContext Context, bool Handled, JsonElement Body)> HandleAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await new ApiExceptionHandler().TryHandleAsync(context, exception, CancellationToken.None);

        context.Response.Body.Position = 0;
        var text = await new StreamReader(context.Response.Body).ReadToEndAsync();
        var body = string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement;
        return (context, handled, body);
    }

    [Fact]
    public async Task AMalformedRequestBody_IsA400_NotA500()
    {
        // What ASP.NET Core throws when the body is not valid JSON for the endpoint's parameter.
        var exception = new BadHttpRequestException("Failed to read parameter \"SomeRequest request\" from the request body as JSON.");

        var (context, handled, body) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("Invalid request.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheAnswerDoesNotEchoTheFrameworksOwnMessage()
    {
        // The message names the endpoint's internal parameter and type, and for a query value it
        // quotes what the caller sent. None of that belongs in the answer.
        var exception = new BadHttpRequestException("Failed to read parameter \"LoginRequest request\" from the request body as JSON. secret-internal-detail");

        var (_, _, body) = await HandleAsync(exception);

        var detail = body.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.DoesNotContain("LoginRequest", detail);
        Assert.DoesNotContain("secret-internal-detail", detail);
    }

    [Theory]
    [InlineData(StatusCodes.Status413PayloadTooLarge)]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    public async Task AClientErrorTheFrameworkAlreadyChose_IsKept(int status)
    {
        var (context, handled, body) = await HandleAsync(new BadHttpRequestException("too slow or too big", status));

        Assert.True(handled);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status, body.GetProperty("status").GetInt32());
    }

    [Theory]
    [InlineData(StatusCodes.Status500InternalServerError)]
    [InlineData(StatusCodes.Status200OK)]
    public async Task AStatusThatIsNotAClientError_BecomesA400(int status)
    {
        // A "bad request" exception must never turn into a success, or hide behind a 5xx.
        var (context, _, _) = await HandleAsync(new BadHttpRequestException("odd", status));

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task AnArgumentProblem_IsStillA400WithItsOwnMessage()
    {
        var (context, handled, body) = await HandleAsync(new ArgumentException("Quantity must be positive."));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("Quantity must be positive.", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsLeftAlone_SoItStaysAnError()
    {
        var (context, handled, _) = await HandleAsync(new InvalidOperationException("a real bug"));

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);   // untouched: the framework turns it into a 500
    }

    [Fact]
    public async Task ABadSignIn_IsStillA401()
    {
        var (context, handled, _) = await HandleAsync(new UserService.Application.InvalidCredentialsException());

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }
}
