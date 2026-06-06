using System.Net;
using System.Net.Http.Json;
using OrderService.Models;

namespace OrderService.Services;

public class StoreApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<StoreApiClient> _logger;

    public StoreApiClient(HttpClient httpClient, ILogger<StoreApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ProductInfo?> GetProductAsync(int productId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching product {ProductId} from StoreApi", productId);

        using var response = await _httpClient.GetAsync($"api/products/{productId}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("StoreApi product {ProductId} not found", productId);
            return null;
        }

        response.EnsureSuccessStatusCode();

        var product = await response.Content.ReadFromJsonAsync<ProductInfo>(cancellationToken: cancellationToken);
        return product;
    }
}
