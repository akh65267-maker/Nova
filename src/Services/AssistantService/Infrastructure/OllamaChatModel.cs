using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssistantService.Application;

namespace AssistantService.Infrastructure;

/// <summary>Talks to a local Ollama server (POST /api/chat, no streaming).</summary>
public sealed class OllamaChatModel(HttpClient http, IConfiguration configuration) : IChatModel
{
    public async Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = configuration["Assistant:Model"] ?? "qwen2.5:3b",
            ["stream"] = false,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray()),
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()),
                },
            }).ToArray()),
        };

        using var response = await http.PostAsJsonAsync("/api/chat", body, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var message = document.RootElement.GetProperty("message");

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("tool_calls", out var raw) && raw.ValueKind == JsonValueKind.Array)
            foreach (var call in raw.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var arguments = function.TryGetProperty("arguments", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                calls.Add(new ToolCall(function.GetProperty("name").GetString() ?? "", arguments));
            }

        return new ChatMessage("assistant", message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "", calls);
    }

    private static JsonNode ToJson(ChatMessage m)
    {
        var node = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolName is not null) node["tool_name"] = m.ToolName;
        if (m.ToolCalls is { Count: > 0 })
            node["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = JsonNode.Parse(c.Arguments.GetRawText()) },
            }).ToArray());
        return node;
    }
}
