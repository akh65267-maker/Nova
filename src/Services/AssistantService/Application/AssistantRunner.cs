namespace AssistantService.Application;

public sealed record AssistantReply(string Reply, IReadOnlyList<string> ToolsUsed);

/// <summary>Asks the model, runs the read-only tools it asks for, and gives the results back until it answers in words.</summary>
public sealed class AssistantRunner(IChatModel model, IEnumerable<IAssistantTool> tools)
{
    public const int MaxMessageLength = 1000;
    public const int MaxRounds = 4;

    private const string SystemPrompt =
        "You are the shop assistant for an online store. Answer briefly. " +
        "Use the tools to look up products, the customer's basket and their own past orders. When asked about products, call search_products " +
        "straight away (a vague word is a fine search) instead of asking questions back. To suggest something, search for what fits the need, " +
        "and where it helps check what the customer usually buys with get_my_orders; recommend only products the tools returned, " +
        "and say why each fits. Never invent products or prices. " +
        "You are not a medical professional. If someone mentions pain or a health problem, your reply MUST begin by saying that a doctor or " +
        "physiotherapist is the right person for that; after that, suggest products by general features that the product descriptions mention " +
        "(for example cushioning or support), never as a treatment. Search with a plain keyword for the kind of product (for example 'running'), not the problem. " +
        "You can only read: you cannot place orders or change anything. If a tool fails, say so.";

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
