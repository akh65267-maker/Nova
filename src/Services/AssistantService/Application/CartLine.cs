namespace AssistantService.Application;

/// <summary>One line of the cart as the shop shows it. The shop keeps the cart in the browser, so the browser sends it with the question; it is the customer's own view and only ever read back to them.</summary>
public sealed record CartLine(string? Name, decimal Price, int Quantity);
