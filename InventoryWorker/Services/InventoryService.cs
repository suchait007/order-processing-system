using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace InventoryWorker.Services;

public class InventoryService
{
    private readonly string _connectionString;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(IConfiguration configuration, ILogger<InventoryService> logger)
    {
        _connectionString = configuration.GetConnectionString("StoreApiDb")
            ?? throw new InvalidOperationException("StoreApiDb connection string is not configured.");
        _logger = logger;
    }

    /// <summary>
    /// Atomically decrements stock and returns the new stock level.
    /// Returns null if insufficient stock or product not found.
    /// Uses OUTPUT clause to get the post-update value in one atomic statement.
    /// </summary>
    public async Task<StockDecrementResult> DecrementStockAsync(
        Guid orderId, int productId, int quantity, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // Idempotency check: skip if this order was already processed
            var alreadyProcessed = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM [dbo].[ProcessedInventoryEvents] WHERE [OrderId] = @OrderId",
                new { OrderId = orderId },
                transaction);

            if (alreadyProcessed > 0)
            {
                _logger.LogWarning("Order {OrderId} already processed — skipping duplicate", orderId);
                await transaction.CommitAsync(cancellationToken);
                return StockDecrementResult.AlreadyProcessed();
            }

            // Atomic stock decrement with OUTPUT to get the new value
            var result = await connection.QuerySingleOrDefaultAsync<StockUpdateRow>(
                @"UPDATE [dbo].[Products]
                  SET [StockQuantity] = [StockQuantity] - @Quantity
                  OUTPUT INSERTED.[StockQuantity] AS NewStock, INSERTED.[Name] AS ProductName,
                         (INSERTED.[StockQuantity] + @Quantity) AS PreviousStock
                  WHERE [Id] = @ProductId
                    AND [StockQuantity] >= @Quantity",
                new { ProductId = productId, Quantity = quantity },
                transaction);

            if (result is null)
            {
                // Either product not found or insufficient stock
                var exists = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM [dbo].[Products] WHERE [Id] = @ProductId",
                    new { ProductId = productId },
                    transaction);

                // Record the processing attempt for idempotency
                await connection.ExecuteAsync(
                    @"INSERT INTO [dbo].[ProcessedInventoryEvents] ([OrderId], [ProductId], [Success], [ProcessedAt])
                      VALUES (@OrderId, @ProductId, 0, SYSUTCDATETIME())",
                    new { OrderId = orderId, ProductId = productId },
                    transaction);

                await transaction.CommitAsync(cancellationToken);

                if (exists == 0)
                {
                    _logger.LogWarning("Product {ProductId} not found for order {OrderId}", productId, orderId);
                    return StockDecrementResult.ProductNotFound();
                }

                _logger.LogWarning("Insufficient stock for product {ProductId}, order {OrderId}", productId, orderId);
                return StockDecrementResult.InsufficientStock();
            }

            // Record successful processing for idempotency
            await connection.ExecuteAsync(
                @"INSERT INTO [dbo].[ProcessedInventoryEvents] ([OrderId], [ProductId], [Success], [ProcessedAt])
                  VALUES (@OrderId, @ProductId, 1, SYSUTCDATETIME())",
                new { OrderId = orderId, ProductId = productId },
                transaction);

            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Decremented stock for product {ProductId}: {PreviousStock} → {NewStock} (order {OrderId})",
                productId, result.PreviousStock, result.NewStock, orderId);

            return StockDecrementResult.Succeeded(result.NewStock, result.PreviousStock, result.ProductName);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private sealed class StockUpdateRow
    {
        public int NewStock { get; set; }
        public int PreviousStock { get; set; }
        public string ProductName { get; set; } = string.Empty;
    }
}

public sealed class StockDecrementResult
{
    public bool Success { get; private init; }
    public bool WasAlreadyProcessed { get; private init; }
    public bool WasProductNotFound { get; private init; }
    public int NewStock { get; private init; }
    public int PreviousStock { get; private init; }
    public string ProductName { get; private init; } = string.Empty;

    public static StockDecrementResult Succeeded(int newStock, int previousStock, string productName) =>
        new() { Success = true, NewStock = newStock, PreviousStock = previousStock, ProductName = productName };

    public static StockDecrementResult InsufficientStock() =>
        new() { Success = false };

    public static StockDecrementResult ProductNotFound() =>
        new() { Success = false, WasProductNotFound = true };

    public static StockDecrementResult AlreadyProcessed() =>
        new() { WasAlreadyProcessed = true, Success = true };
}
