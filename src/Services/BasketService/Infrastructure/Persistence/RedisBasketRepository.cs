using System.Text.Json;
using BasketService.Domain;
using StackExchange.Redis;

namespace BasketService.Infrastructure.Persistence;

public class RedisBasketRepository : IBasketRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;

    public RedisBasketRepository(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    private static string KeyFor(Guid userId) => $"basket:{userId}";

    public async Task<Basket?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(KeyFor(userId));

        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Basket>((string)value!, JsonOptions);
    }

    public async Task SaveAsync(Basket basket, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(basket, JsonOptions);

        await db.StringSetAsync(KeyFor(basket.UserId), json);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();

        await db.KeyDeleteAsync(KeyFor(userId));
    }
}
