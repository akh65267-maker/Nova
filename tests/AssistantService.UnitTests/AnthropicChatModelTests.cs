using System.Net;
using System.Text;
using System.Text.Json;
using AssistantService.Application;
using AssistantService.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AssistantService.UnitTests;

public class AnthropicChatModelTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public JsonElement Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Path = request.RequestUri!.AbsolutePath;
            Sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static AnthropicChatModel Model(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://anthropic/") },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Assistant:Anthropic:ApiKey"] = "test-key",
                ["Assistant:Anthropic:Model"] = "tiny",
            }).Build());

    private static readonly ToolSpec Search = new("search_products", "find things", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task ASpokenAnswer_IsReturnedAsText_AndTheRequestCarriesTheKeyModelSystemPromptAndTools()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"content":[{"type":"text","text":"Hi "},{"type":"text","text":"there"}]}""");

        var answer = await Model(stub).CompleteAsync(
            [new ChatMessage("system", "Be kind."), new ChatMessage("system", "Cart is empty."), new ChatMessage("user", "hi")], [Search], CancellationToken.None);

        Assert.Equal("Hi there", answer.Content);
        Assert.Empty(answer.ToolCalls!);
        Assert.Equal("/v1/messages", stub.Path);
        Assert.Equal("test-key", stub.Request!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", stub.Request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("tiny", stub.Sent.GetProperty("model").GetString());
        Assert.Equal("Be kind.\n\nCart is empty.", stub.Sent.GetProperty("system").GetString());
        var only = Assert.Single(stub.Sent.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", only.GetProperty("role").GetString());
        Assert.Equal("search_products", stub.Sent.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ARequestToCallATool_IsReadWithItsArguments()
    {
        var stub = new Stub(HttpStatusCode.OK,
            """{"content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"toolu_x","name":"search_products","input":{"search":"lamp","maxPrice":20}}]}""");

        var answer = await Model(stub).CompleteAsync([new ChatMessage("user", "a lamp under 20")], [Search], CancellationToken.None);

        var call = Assert.Single(answer.ToolCalls!);
        Assert.Equal("search_products", call.Name);
        Assert.Equal("lamp", call.Arguments.GetProperty("search").GetString());
        Assert.Equal("Let me look.", answer.Content);
    }

    [Fact]
    public async Task ToolResults_AreSentBackAgainstTheCallsTheyAnswer_TogetherInOneUserTurn()
    {
        var stub = new Stub(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}]}""");
        var history = new List<ChatMessage>
        {
            new("user", "what do I own?"),
            new("assistant", "", [new ToolCall("search_products", Json("""{"search":"a"}""")), new ToolCall("get_my_orders", Json("{}"))]),
            new("tool", "two lamps", ToolName: "search_products"),
            new("tool", "no orders", ToolName: "get_my_orders"),
        };

        await Model(stub).CompleteAsync(history, [Search], CancellationToken.None);

        var sent = stub.Sent.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, sent.Count);
        var uses = sent[1].GetProperty("content").EnumerateArray().ToList();
        var results = sent[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("user", sent[2].GetProperty("role").GetString());
        Assert.Equal(uses[0].GetProperty("id").GetString(), results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal(uses[1].GetProperty("id").GetString(), results[1].GetProperty("tool_use_id").GetString());
        Assert.NotEqual(uses[0].GetProperty("id").GetString(), uses[1].GetProperty("id").GetString());
        Assert.Equal("no orders", results[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AFailingApi_Throws_SoTheServiceCanAnswerThatTheModelIsDown()
    {
        var stub = new Stub(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => Model(stub).CompleteAsync([new ChatMessage("user", "hi")], [Search], CancellationToken.None));
    }
}
