namespace BasketService.Application.Baskets;

public sealed record ReplaceBasketRequest(IReadOnlyCollection<AddBasketItemRequest>? Items);
