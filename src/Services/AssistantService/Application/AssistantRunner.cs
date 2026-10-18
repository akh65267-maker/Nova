namespace AssistantService.Application;

public sealed record AssistantReply(string Reply, IReadOnlyList<string> ToolsUsed);

/// <summary>Asks the model, runs the read-only tools it asks for, and gives the results back until it answers in words.</summary>
public sealed class AssistantRunner(IChatModel model, IEnumerable<IAssistantTool> tools)
{
    public const int MaxMessageLength = 1000;
    public const int MaxRounds = 4;

    private const string SystemPrompt =
        "You are the shop assistant for an online store. Answer briefly. Use the tools to look up products and the customer's basket; " +
        "when asked about products, call search_products straight away (a vague word is a fine search) instead of asking questions back; never invent products or prices. You can only read: you cannot place orders or change anything. If a tool fails, say so.";

    private readonly IReadOnlyDictionary<string, IAssistantTool> _tools = tools.ToDictionary(t => t.Spec.Name);

    public async Task<AssistantReply> AskAsync(string message, string? authorization, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage> { new("system", SystemPrompt), new("user", message) };
        var used = new List<string>();
        var specs = _tools.Values.Select(t => t.Spec).ToList();

        for (var round = 0; round < MaxRounds; round++)
        {
            var answer = await model.CompleteAsync(messages, specs, cancellationToken);
            if (answer.ToolCalls is not { Count: > 0 })
                return new AssistantReply(answer.Content, used);

            messages.Add(answer);
            foreach (var call in answer.ToolCalls)
            {
                var result = _tools.TryGetValue(call.Name, out var tool)
                    ? await tool.RunAsync(call.Arguments, authorization, cancellationToken)
                    : "There is no such tool.";
                used.Add(call.Name);
                messages.Add(new ChatMessage("tool", result, ToolName: call.Name));
            }
        }

        return new AssistantReply("I could not finish looking that up. Please try a simpler question.", used);
    }
}
