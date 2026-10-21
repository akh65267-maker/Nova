using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AssistantService.Application;

/// <summary>
/// One of our read-only tools as an Agent Framework function. Made per request, so it carries that caller's
/// token (never another's) and notes each call for the reply's ToolsUsed.
/// </summary>
public sealed class AssistantToolFunction(IAssistantTool tool, string? authorization, ICollection<string> used) : AIFunction
{
    public override string Name => tool.Spec.Name;
    public override string Description => tool.Spec.Description;
    public override JsonElement JsonSchema => tool.Spec.Parameters;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        lock (used) used.Add(Name);
        var json = JsonSerializer.SerializeToElement(arguments.ToDictionary(a => a.Key, a => a.Value));
        return await tool.RunAsync(json, authorization, cancellationToken);
    }
}
