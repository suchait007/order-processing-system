using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using OrderService.Models;

namespace OrderService.Services;

public class RedisCacheService
{
    private const int ProductCacheMinutes = 5;
    private const int OrderStatusCacheMinutes = 30;
    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheService> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public RedisCacheService(IDistributedCache cache, ILogger<RedisCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<ProductInfo?> GetProductInfoAsync(int productId, CancellationToken cancellationToken = default)
    {
        var cacheKey = GetProductCacheKey(productId);
        var cachedJson = await _cache.GetStringAsync(cacheKey, cancellationToken);

        if (string.IsNullOrWhiteSpace(cachedJson))
        {
            return null;
        }

        _logger.LogInformation("Cache hit for product {ProductId}", productId);
        return JsonSerializer.Deserialize<ProductInfo>(cachedJson, SerializerOptions);
    }

    public async Task SetProductInfoAsync(int productId, ProductInfo info, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var cacheKey = GetProductCacheKey(productId);
        var serializedValue = JsonSerializer.Serialize(info, SerializerOptions);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromMinutes(ProductCacheMinutes)
        };

        await _cache.SetStringAsync(cacheKey, serializedValue, options, cancellationToken);
        _logger.LogInformation("Cached product {ProductId} for {Ttl}", productId, options.AbsoluteExpirationRelativeToNow);
    }

    public Task<string?> GetOrderStatusAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        _cache.GetStringAsync(GetOrderStatusCacheKey(orderId), cancellationToken);

    public async Task SetOrderStatusAsync(Guid orderId, string status, CancellationToken cancellationToken = default)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(OrderStatusCacheMinutes)
        };

        await _cache.SetStringAsync(GetOrderStatusCacheKey(orderId), status, options, cancellationToken);
        _logger.LogInformation("Cached status {Status} for order {OrderId}", status, orderId);
    }

    private static string GetProductCacheKey(int productId) => $"product-info:{productId}";

    private static string GetOrderStatusCacheKey(Guid orderId) => $"order-status:{orderId}";
}
