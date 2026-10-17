using System.Text.Json.Serialization;

namespace InventoryService.Application.Inventory;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StockStatus
{
    InStock,
    LowStock,
    OutOfStock,
}

/// <summary>
/// What anyone may learn about a product's stock without signing in: a status, and the exact
/// number only when it is small. Exact levels for every product would let a competitor watch how
/// fast stock sells; the full list with exact numbers is for administrators.
/// </summary>
/// <param name="Remaining">The exact quantity when it is <see cref="InventoryItemsService.ExactUpTo"/> or fewer, otherwise null.</param>
public sealed record AvailabilityDto(Guid ProductId, StockStatus Availability, int? Remaining);
