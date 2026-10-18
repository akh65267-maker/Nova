using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AssistantService.Application;

public sealed class SearchProductsTool(IHttpClientFactory clients) : IAssistantTool
{
    public const string HttpClientName = "catalog";

    public ToolSpec Spec { get; } = new(
        "search_products",
        "Search the shop's catalog by name or description. It is a plain text match, not a smart search: give ONE or TWO short keywords such as 'running' or 'insoles', never a sentence and never the customer's problem. Returns up to 5 products with id, name, description, price and category.",
        JsonDocument.Parse("""{"type":"object","properties":{"search":{"type":"string"},"category":{"type":"string"},"maxPrice":{"type":"number"}},"required":["search"]}""").RootElement.Clone());

    public async Task<string> RunAsync(JsonElement arguments, string? authorization, CancellationToken cancellationToken)
    {
        var query = new List<string> { "pageSize=5" };
        if (Text(arguments, "search") is { } search) query.Add("search=" + Uri.EscapeDataString(search));
        if (Text(arguments, "category") is { } category) query.Add("category=" + Uri.EscapeDataString(category));
        if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("maxPrice", out var max) && max.ValueKind == JsonValueKind.Number && max.TryGetDecimal(out var price))
            query.Add("maxPrice=" + price.ToString(CultureInfo.InvariantCulture));

        // The catalog is public: the caller's token is not sent.
        return await ToolHttp.GetAsync(clients.CreateClient(HttpClientName), "/products?" + string.Join('&', query), null, cancellationToken);
    }

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}

public sealed class GetMyBasketTool(IHttpClientFactory clients) : IAssistantTool
{
    public const string HttpClientName = "basket";

    public ToolSpec Spec { get; } = new(
        "get_my_basket",
        "Read the signed-in customer's own basket.",
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone());

    public Task<string> RunAsync(JsonElement arguments, string? authorization, CancellationToken cancellationToken) =>
        ToolHttp.GetAsync(clients.CreateClient(HttpClientName), "/basket", authorization, cancellationToken);
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
