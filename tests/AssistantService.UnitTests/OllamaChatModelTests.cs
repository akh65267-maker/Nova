using System.Net;
using System.Text;
using System.Text.Json;
using AssistantService.Application;
using AssistantService.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AssistantService.UnitTests;

public class OllamaChatModelTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public JsonElement Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static OllamaChatModel Model(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://ollama/") },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:Model"] = "tiny" }).Build());

    private static readonly ToolSpec Search = new("search_products", "find things", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    [Fact]
    public async Task ASpokenAnswer_IsReturnedAsTextWithNoToolCalls()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"message":{"role":"assistant","content":"Hi there"}}""");

        var answer = await Model(stub).CompleteAsync([new ChatMessage("user", "hi")], [Search], CancellationToken.None);

        Assert.Equal("Hi there", answer.Content);
        Assert.Empty(answer.ToolCalls!);
        Assert.Equal("/api/chat", stub.Path);
        Assert.Equal("tiny", stub.Sent.GetProperty("model").GetString());
        Assert.False(stub.Sent.GetProperty("stream").GetBoolean());
        Assert.Equal("search_products", stub.Sent.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ARequestToCallATool_IsReadWithItsArguments()
    {
        var stub = new Stub(HttpStatusCode.OK,
            """{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"search_products","arguments":{"search":"lamp","maxPrice":20}}}]}}""");

        var answer = await Model(stub).CompleteAsync([new ChatMessage("user", "a lamp under 20")], [Search], CancellationToken.None);

        var call = Assert.Single(answer.ToolCalls!);
        Assert.Equal("search_products", call.Name);
        Assert.Equal("lamp", call.Arguments.GetProperty("search").GetString());
        Assert.Equal(20, call.Arguments.GetProperty("maxPrice").GetInt32());
    }

    [Fact]
    public async Task AToolsResult_IsSentBackAsAToolMessage()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"message":{"role":"assistant","content":"ok"}}""");
        var call = new ToolCall("search_products", JsonDocument.Parse("""{"search":"lamp"}""").RootElement.Clone());

        await Model(stub).CompleteAsync(
            [new ChatMessage("user", "lamp?"), new ChatMessage("assistant", "", [call]), new ChatMessage("tool", "[]", ToolName: "search_products")],
            [Search], CancellationToken.None);

        var sent = stub.Sent.GetProperty("messages");
        Assert.Equal("lamp", sent[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("search").GetString());
        Assert.Equal("tool", sent[2].GetProperty("role").GetString());
        Assert.Equal("search_products", sent[2].GetProperty("tool_name").GetString());
    }

    [Fact]
    public async Task AFailingServer_Throws_SoTheEndpointCanAnswer503()
    {
        var stub = new Stub(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Model(stub).CompleteAsync([new ChatMessage("user", "hi")], [Search], CancellationToken.None));
    }
}
