using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssistantService.Application;

public sealed class SearchProductsTool(IHttpClientFactory clients) : IAssistantTool
{
    public const string HttpClientName = "catalog";
    public const string InventoryClientName = "inventory";

    public ToolSpec Spec { get; } = new(
        "search_products",
        "Search the shop's catalog by name or description. It is a plain text match, not a smart search: give ONE or TWO short keywords such as 'running' or 'insoles', never a sentence and never the customer's problem. Returns up to 5 products that are in stock, with id, name, description, price and category.",
        JsonDocument.Parse("""{"type":"object","properties":{"search":{"type":"string"},"category":{"type":"string"},"maxPrice":{"type":"number"}},"required":["search"]}""").RootElement.Clone());

    public async Task<string> RunAsync(JsonElement arguments, string? authorization, CancellationToken cancellationToken)
    {
        var query = new List<string> { "pageSize=5" };
        if (Text(arguments, "search") is { } search) query.Add("search=" + Uri.EscapeDataString(search));
        if (Text(arguments, "category") is { } category) query.Add("category=" + Uri.EscapeDataString(category));
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("maxPrice", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetDecimal(out var price))
            query.Add("maxPrice=" + price.ToString(CultureInfo.InvariantCulture));

        // The catalog is public: the caller's token is not sent.
        var body = await ToolHttp.GetAsync(clients.CreateClient(HttpClientName), "/products?" + string.Join('&', query), null, cancellationToken);
        return await InStockOnlyAsync(body, cancellationToken);
    }

    /// <summary>
    /// Drops the products that cannot be bought (no stock, or no stock record, as in the shop) so the model
    /// never recommends one. A product whose stock could not be looked up stays: it is not known to be gone.
    /// </summary>
    private async Task<string> InStockOnlyAsync(string catalogBody, CancellationToken cancellationToken)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(catalogBody);
        }
        catch (JsonException)
        {
            return catalogBody; // a failure message, not a list: pass it on as it is
        }

        if (parsed is not JsonObject result || result["items"] is not JsonArray items)
            return catalogBody;

        var inventory = clients.CreateClient(InventoryClientName);
        var kept = new JsonArray();
        foreach (var item in items.OfType<JsonObject>())
        {
            var id = item["id"]?.GetValue<string>();
            if (id is null) continue;
            if (await IsAvailableAsync(inventory, id, cancellationToken) is false) continue;
            kept.Add(item.DeepClone());
        }

        result["items"] = kept;
        result["totalCount"] = kept.Count;
        return result.ToJsonString();
    }

    /// <summary>true/false when the inventory answered, null when it could not be asked.</summary>
    private static async Task<bool?> IsAvailableAsync(HttpClient inventory, string productId, CancellationToken cancellationToken)
    {
        var answer = await ToolHttp.GetAsync(inventory, "/inventory/" + Uri.EscapeDataString(productId), null, cancellationToken);
        if (answer.StartsWith("The lookup failed with status 404", StringComparison.Ordinal)) return false;
        try
        {
            return JsonNode.Parse(answer)?["availability"]?.GetValue<string>() is { } status ? status != "OutOfStock" : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}

/// <summary>The caller's own orders (the order service returns only the signed-in user's), for "what do I usually buy?" questions.</summary>
public sealed class GetMyOrdersTool(IHttpClientFactory clients) : IAssistantTool
{
    public const string HttpClientName = "orders";

    public ToolSpec Spec { get; } = new(
        "get_my_orders",
        "Read the signed-in customer's own past orders (items, totals, status, date) to see what they usually buy.",
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

    public Task<string> RunAsync(JsonElement arguments, string? authorization, CancellationToken cancellationToken) =>
        ToolHttp.GetAsync(clients.CreateClient(HttpClientName), "/orders", authorization, cancellationToken);
}

internal static class ToolHttp
{
    private const int MaxChars = 4000;

    public static async Task<string> GetAsync(HttpClient client, string path, string? authorization, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (AuthenticationHeaderValue.TryParse(authorization, out var header)) request.Headers.Authorization = header;

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return $"The lookup failed with status {(int)response.StatusCode}.";

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return body.Length > MaxChars ? body[..MaxChars] : body;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "The lookup is not available right now.";
        }
    }
}
