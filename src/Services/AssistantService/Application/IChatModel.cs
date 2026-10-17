using System.Text.Json;

namespace AssistantService.Application;

/// <summary>One turn of the conversation. A model's request to call a tool arrives as <see cref="ToolCalls"/>; its result goes back as a "tool" message.</summary>
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolName = null);

public sealed record ToolCall(string Name, JsonElement Arguments);

/// <summary>What the model is allowed to call: a name, what it is for, and a JSON-schema for its arguments.</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement Parameters);

/// <summary>The language model behind the assistant. Swappable: a local Ollama today, a hosted model later.</summary>
public interface IChatModel
{
    Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken);
}
