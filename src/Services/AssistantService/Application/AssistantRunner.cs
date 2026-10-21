using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AssistantService.Application;

public sealed record AssistantReply(string Reply, IReadOnlyList<string> ToolsUsed);

/// <summary>
/// A Microsoft Agent Framework agent over whichever model is configured (local Ollama or the Anthropic API):
/// it asks the model, runs the read-only tools it asks for, and gives the results back until it answers in words.
/// </summary>
public sealed class AssistantRunner(IChatClient model, IEnumerable<IAssistantTool> tools)
{
    public const int MaxMessageLength = 1000;
    public const int MaxRounds = 4;

    private const string SystemPrompt =
        "You are the shop assistant for an online store. Answer briefly. " +
        "Use the tools to look up products and the customer's own past orders; their current cart is given to you below, so answer cart questions from that. When asked about products, call search_products " +
        "straight away (a vague word is a fine search) instead of asking questions back. To suggest something, search for what fits the need, " +
        "and where it helps check what the customer usually buys with get_my_orders; recommend only products the tools returned, " +
        "and say why each fits. Never invent products or prices. " +
        "You are not a medical professional. If someone mentions pain or a health problem, your reply MUST begin by saying that a doctor or " +
        "physiotherapist is the right person for that; after that, suggest products by general features that the product descriptions mention " +
        "(for example cushioning or support), never as a treatment. Search with a plain keyword for the kind of product (for example 'running'), not the problem. " +
        "You can only read: you cannot place orders or change anything. If a tool fails, say so.";

    private const int MaxCartLines = 20;
    private const int MaxNameLength = 80;

    /// <summary>The cart the browser sent, bounded and flattened to one plain line per item (a name is the customer's own text, so no line breaks).</summary>
    public static string DescribeCart(IReadOnlyList<CartLine>? cart)
    {
        if (cart is not { Count: > 0 })
            return "The customer's cart is empty.";

        var lines = cart.Take(MaxCartLines).Select(line =>
        {
            var name = new string((line.Name ?? "an item").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length > MaxNameLength) name = name[..MaxNameLength];
            return $"- {Math.Clamp(line.Quantity, 1, 99)} x {name} at {line.Price:0.00} each";
        });

        return "The customer's cart right now:\n" + string.Join('\n', lines);
    }

    public const string CouldNotFinish = "I could not finish looking that up. Please try a simpler question.";

    // The framework's function-calling loop around the model, capped so a model that keeps asking for tools
    // stops after MaxRounds model calls.
    private readonly IChatClient _client = new ChatClientBuilder(model)
        .UseFunctionInvocation(configure: loop => loop.MaximumIterationsPerRequest = MaxRounds)
        .Build();

    public async Task<AssistantReply> AskAsync(string message, string? authorization, IReadOnlyList<CartLine>? cart, CancellationToken cancellationToken)
    {
        var used = new List<string>();
        var agent = new ChatClientAgent(_client, new ChatClientAgentOptions
        {
            Name = "shop-assistant",
            UseProvidedChatClientAsIs = true, // the loop above is already there; do not wrap a second one
            ChatOptions = new ChatOptions
            {
                Instructions = SystemPrompt + "\n\n" + DescribeCart(cart),
                Tools = tools.Select(t => (AITool)new AssistantToolFunction(t, authorization, used)).ToList(),
            },
        });

        var response = await agent.RunAsync(message, cancellationToken: cancellationToken);

        // Out of rounds, the last answer is a request for yet another tool, with no words in it.
        var reply = response.Text.Trim();
        return new AssistantReply(reply.Length > 0 ? reply : CouldNotFinish, used);
    }
}
