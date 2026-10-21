using System.Text.Json;

namespace AssistantService.Application;

/// <summary>What the model is allowed to call: a name, what it is for, and a JSON-schema for its arguments.</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement Parameters);

/// <summary>A read-only thing the assistant can look up. It runs with the caller's own token, so it can see exactly what the caller can.</summary>
public interface IAssistantTool
{
    ToolSpec Spec { get; }

    /// <summary>Returns text for the model to read. Failures are returned as text too, never thrown at the model.</summary>
    Task<string> RunAsync(JsonElement arguments, string? authorization, CancellationToken cancellationToken);
}
