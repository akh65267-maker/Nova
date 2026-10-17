using System.Text.Json;
using AssistantService.Application;

namespace AssistantService.UnitTests;

public class AssistantRunnerTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // A model that plays back a script and records what it was asked.
    private sealed class ScriptedModel(params ChatMessage[] script) : IChatModel
    {
        private int _next;
        public List<IReadOnlyList<ChatMessage>> Seen { get; } = [];

        public Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken)
        {
            Seen.Add(messages.ToList());
            return Task.FromResult(script[Math.Min(_next++, script.Length - 1)]);
        }
    }

    private sealed class FakeTool(string name, string result) : IAssistantTool
    {
        public List<(JsonElement Arguments, string? Authorization)> Calls { get; } = [];
        public ToolSpec Spec { get; } = new(name, "a tool", Json("{}"));

        public Task<string> RunAsync(JsonElement arguments, string? authorization, CancellationToken cancellationToken)
        {
            Calls.Add((arguments, authorization));
            return Task.FromResult(result);
        }
    }

    private static ChatMessage Calls(string tool, string arguments = "{}") => new("assistant", "", [new ToolCall(tool, Json(arguments))]);

    [Fact]
    public async Task AnAnswerInWords_IsReturnedWithoutRunningAnyTool()
    {
        var tool = new FakeTool("search_products", "[]");
        var runner = new AssistantRunner(new ScriptedModel(new ChatMessage("assistant", "Hello!")), [tool]);

        var reply = await runner.AskAsync("hi", "Bearer t", CancellationToken.None);

        Assert.Equal("Hello!", reply.Reply);
        Assert.Empty(reply.ToolsUsed);
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task ARequestedTool_RunsWithTheCallersTokenAndItsResultGoesBackToTheModel()
    {
        var tool = new FakeTool("get_my_basket", "2 x Widget");
        var model = new ScriptedModel(Calls("get_my_basket"), new ChatMessage("assistant", "You have 2 widgets."));
        var runner = new AssistantRunner(model, [tool]);

        var reply = await runner.AskAsync("what is in my basket?", "Bearer caller-token", CancellationToken.None);

        Assert.Equal("You have 2 widgets.", reply.Reply);
        Assert.Equal(["get_my_basket"], reply.ToolsUsed);
        Assert.Equal("Bearer caller-token", Assert.Single(tool.Calls).Authorization);
        var second = model.Seen[1];
        Assert.Equal("tool", second[^1].Role);
        Assert.Equal("2 x Widget", second[^1].Content);
    }

    [Fact]
    public async Task AToolThatDoesNotExist_IsAnsweredAsSuchAndNothingRuns()
    {
        var tool = new FakeTool("search_products", "[]");
        var model = new ScriptedModel(Calls("delete_everything"), new ChatMessage("assistant", "Sorry."));

        var reply = await new AssistantRunner(model, [tool]).AskAsync("do it", null, CancellationToken.None);

        Assert.Equal("Sorry.", reply.Reply);
        Assert.Empty(tool.Calls);
        Assert.Equal("There is no such tool.", model.Seen[1][^1].Content);
    }

    [Fact]
    public async Task AModelThatNeverStopsAskingForTools_IsStoppedAfterTheRoundLimit()
    {
        var tool = new FakeTool("search_products", "[]");
        var model = new ScriptedModel(Calls("search_products"));

        var reply = await new AssistantRunner(model, [tool]).AskAsync("loop", null, CancellationToken.None);

        Assert.Equal(AssistantRunner.MaxRounds, model.Seen.Count);
        Assert.Equal(AssistantRunner.MaxRounds, tool.Calls.Count);
        Assert.Contains("could not finish", reply.Reply);
    }
}
