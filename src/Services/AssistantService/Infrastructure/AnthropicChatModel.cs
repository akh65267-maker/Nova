using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssistantService.Application;

namespace AssistantService.Infrastructure;

/// <summary>
/// Talks to the Anthropic Messages API (POST /v1/messages, no streaming). Chosen with
/// Assistant:Provider = "anthropic"; the key comes from Assistant:Anthropic:ApiKey and is never logged.
/// </summary>
public sealed class AnthropicChatModel(HttpClient http, IConfiguration configuration) : IChatModel
{
    public const string DefaultModel = "claude-haiku-5-5";
    private const int MaxTokens = 1024;

    public async Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = configuration["Assistant:Anthropic:Model"] ?? DefaultModel,
            ["max_tokens"] = MaxTokens,
            ["system"] = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content)),
            ["messages"] = ToApiMessages(messages),
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.Parameters.GetRawText()),
            }).ToArray()),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages") { Content = JsonContent.Create(body) };
        request.Headers.Add("x-api-key", configuration["Assistant:Anthropic:ApiKey"]);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var text = new List<string>();
        var calls = new List<ToolCall>();
        foreach (var block in document.RootElement.GetProperty("content").EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    text.Add(block.GetProperty("text").GetString() ?? "");
                    break;
                case "tool_use":
                    calls.Add(new ToolCall(block.GetProperty("name").GetString() ?? "", block.GetProperty("input").Clone()));
                    break;
            }
        }

        return new ChatMessage("assistant", string.Concat(text), calls);
    }

    // The runner's messages carry no tool-call ids (Ollama has none), so each call is numbered as it is
    // sent and each result takes the next number; results that follow one another share a single user turn.
    private static JsonArray ToApiMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new JsonArray();
        var pendingIds = new Queue<string>();
        var counter = 0;

        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            switch (m.Role)
            {
                case "assistant":
                    var blocks = new JsonArray();
                    if (m.Content.Length > 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                    foreach (var call in m.ToolCalls ?? [])
                    {
                        var id = $"toolu_{++counter}";
                        pendingIds.Enqueue(id);
                        blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = call.Name, ["input"] = JsonNode.Parse(call.Arguments.GetRawText()) });
                    }
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
                    break;

                case "tool":
                    var toolResult = new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = pendingIds.Count > 0 ? pendingIds.Dequeue() : $"toolu_{++counter}",
                        ["content"] = m.Content,
                    };
                    if (result.Count > 0 && result[^1] is JsonObject last && last["role"]?.GetValue<string>() == "user"
                        && last["content"] is JsonArray existing && existing.Count > 0 && existing[0]?["type"]?.GetValue<string>() == "tool_result")
                        existing.Add(toolResult);
                    else
                        result.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(toolResult) });
                    break;

                default:
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Content }) });
                    break;
            }
        }

        return result;
    }
}
