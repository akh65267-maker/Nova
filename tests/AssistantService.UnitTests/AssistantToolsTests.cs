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

    // Answers the catalog and the inventory differently: the inventory by product id (a missing id is a 404).
    private sealed class Shop(string catalogBody, Dictionary<string, (HttpStatusCode Status, string Body)> stock) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(name, catalogBody, stock)) { BaseAddress = new Uri("http://service/") };

        private sealed class Handler(string name, string catalogBody, Dictionary<string, (HttpStatusCode Status, string Body)> stock) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (name == SearchProductsTool.HttpClientName)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(catalogBody) });

                var id = request.RequestUri!.AbsolutePath.Split('/').Last();
                var (status, body) = stock.TryGetValue(id, out var found) ? found : (HttpStatusCode.NotFound, "");
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
            }
        }
    }

    private const string Catalog = """{"items":[{"id":"in","name":"Insoles"},{"id":"low","name":"Socks"},{"id":"out","name":"Shoes"},{"id":"none","name":"Laces"},{"id":"down","name":"Cap"}],"totalCount":5}""";

    [Fact]
    public async Task SearchProducts_LeavesOutWhatCannotBeBought_AndKeepsWhatItCouldNotCheck()
    {
        var stock = new Dictionary<string, (HttpStatusCode, string)>
        {
            ["in"] = (HttpStatusCode.OK, """{"productId":"in","availability":"InStock","remaining":null}"""),
            ["low"] = (HttpStatusCode.OK, """{"productId":"low","availability":"LowStock","remaining":2}"""),
            ["out"] = (HttpStatusCode.OK, """{"productId":"out","availability":"OutOfStock","remaining":0}"""),
            // "none" has no record (404); "down" is a server error: not known to be gone, so it stays.
            ["down"] = (HttpStatusCode.InternalServerError, ""),
        };

        var result = await new SearchProductsTool(new Shop(Catalog, stock)).RunAsync(NoArguments, null, CancellationToken.None);

        var parsed = JsonDocument.Parse(result).RootElement;
        Assert.Equal(["in", "low", "down"], parsed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(3, parsed.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SearchProducts_AFailedCatalogLookup_ReachesTheModelUnchanged()
    {
        var result = await new SearchProductsTool(new Factory(new Recorder(HttpStatusCode.BadGateway))).RunAsync(NoArguments, null, CancellationToken.None);

        Assert.Equal("The lookup failed with status 502.", result);
    }

    [Fact]
    public async Task AFailingLookup_IsReportedAsTextForTheModel_NotThrown()
    {
        var result = await new GetMyOrdersTool(new Factory(new Recorder(HttpStatusCode.Unauthorized))).RunAsync(NoArguments, null, CancellationToken.None);

        Assert.Equal("The lookup failed with status 401.", result);
    }
}
