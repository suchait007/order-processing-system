using Microsoft.Extensions.Caching.Distributed;

namespace InventoryWorker.Services;

public class RedisCacheService
{
    private const int StockCacheMinutes = 10;
    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IDistributedCache cache, ILogger<RedisCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<int?> GetStockLevelAsync(int productId, CancellationToken cancellationToken = default)
    {
        var cached = await _cache.GetStringAsync(GetStockKey(productId), cancellationToken);

        if (cached is not null && int.TryParse(cached, out var stock))
        {
            _logger.LogDebug("Cache hit for stock level of product {ProductId}: {Stock}", productId, stock);
            return stock;
        }

        return null;
    }

    public async Task SetStockLevelAsync(int productId, int stockLevel, CancellationToken cancellationToken = default)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(StockCacheMinutes)
        };

        await _cache.SetStringAsync(GetStockKey(productId), stockLevel.ToString(), options, cancellationToken);
        _logger.LogInformation("Cached stock level for product {ProductId}: {Stock}", productId, stockLevel);
    }

    private static string GetStockKey(int productId) => $"stock-level:{productId}";
}
