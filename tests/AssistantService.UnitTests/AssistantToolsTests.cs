using System.Net;
using System.Text.Json;
using AssistantService.Application;

namespace AssistantService.UnitTests;

public class AssistantToolsTests
{
    private sealed class Recorder(HttpStatusCode status = HttpStatusCode.OK, string body = "[]") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(Recorder handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://service/") };
    }

    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement.Clone();

    [Fact]
    public async Task MyOrders_AsksTheOrderServiceForTheCallersOwnOrdersWithTheCallersToken()
    {
        var recorder = new Recorder(body: """[{"id":"1","totalAmount":19.99}]""");

        var result = await new GetMyOrdersTool(new Factory(recorder)).RunAsync(NoArguments, "Bearer caller-token", CancellationToken.None);

        Assert.Equal("/orders", recorder.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer caller-token", recorder.Request.Headers.Authorization!.ToString());
        Assert.Contains("19.99", result);
    }

    [Fact]
    public async Task SearchProducts_SendsNoTokenAndEscapesWhatTheModelTyped()
    {
        var recorder = new Recorder();
        var arguments = JsonDocument.Parse("""{"search":"running shoes & socks","maxPrice":80}""").RootElement.Clone();

        await new SearchProductsTool(new Factory(recorder)).RunAsync(arguments, "Bearer caller-token", CancellationToken.None);

        Assert.Null(recorder.Request!.Headers.Authorization);
        Assert.Equal("?pageSize=5&search=running%20shoes%20%26%20socks&maxPrice=80", recorder.Request.RequestUri!.Query);
    }

    [Fact]
    public async Task AFailingLookup_IsReportedAsTextForTheModel_NotThrown()
    {
        var result = await new GetMyOrdersTool(new Factory(new Recorder(HttpStatusCode.Unauthorized))).RunAsync(NoArguments, null, CancellationToken.None);

        Assert.Equal("The lookup failed with status 401.", result);
    }
}
