using System.Text.Json;
using Confluent.Kafka;
using InventoryWorker.Models;

namespace InventoryWorker.Services;

public class OrderPlacedConsumer : BackgroundService
{
    private readonly ConsumerConfig _consumerConfig;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<OrderPlacedConsumer> _logger;
    private readonly int _lowStockThreshold;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public OrderPlacedConsumer(
        IConfiguration configuration,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<OrderPlacedConsumer> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _lowStockThreshold = configuration.GetValue("Inventory:LowStockThreshold", 10);

        var bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka bootstrap servers are not configured.");

        _consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "inventory-worker",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        KafkaSecurity.Apply(_consumerConfig, configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield to allow host startup to complete
        await Task.Yield();

        using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
        consumer.Subscribe("order-placed");

        _logger.LogInformation("InventoryWorker subscribed to order-placed topic (threshold={Threshold})",
            _lowStockThreshold);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? consumeResult;

                try
                {
                    consumeResult = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error");
                    continue;
                }

                if (consumeResult?.Message?.Value is null)
                {
                    continue;
                }

                try
                {
                    await ProcessOrderPlacedAsync(consumeResult.Message.Value, stoppingToken);
                    consumer.Commit(consumeResult);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process order-placed message: {Message}",
                        consumeResult.Message.Value);
                    // Don't commit — message will be redelivered on next poll
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InventoryWorker consumer is stopping");
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProcessOrderPlacedAsync(string messageValue, CancellationToken cancellationToken)
    {
        var orderEvent = JsonSerializer.Deserialize<OrderPlacedEvent>(messageValue, SerializerOptions);

        if (orderEvent is null || orderEvent.OrderId == Guid.Empty)
        {
            _logger.LogWarning("Received invalid order-placed message: {Message}", messageValue);
            return;
        }

        _logger.LogInformation(
            "Processing order-placed: OrderId={OrderId} ProductId={ProductId} Quantity={Quantity}",
            orderEvent.OrderId, orderEvent.ProductId, orderEvent.Quantity);

        using var scope = _serviceScopeFactory.CreateScope();
        var inventoryService = scope.ServiceProvider.GetRequiredService<InventoryService>();
        var kafkaProducer = scope.ServiceProvider.GetRequiredService<KafkaProducerService>();
        var cacheService = scope.ServiceProvider.GetRequiredService<RedisCacheService>();

        var result = await inventoryService.DecrementStockAsync(
            orderEvent.OrderId, orderEvent.ProductId, orderEvent.Quantity, cancellationToken);

        if (result.WasAlreadyProcessed)
        {
            _logger.LogInformation("Order {OrderId} was already processed — publishing cached result", orderEvent.OrderId);
            // Re-publish inventory-updated in case the previous publish failed
            await kafkaProducer.PublishInventoryUpdatedAsync(
                new InventoryUpdatedEvent(
                    orderEvent.OrderId, orderEvent.ProductId,
                    "Confirmed", true, result.NewStock, DateTime.UtcNow),
                cancellationToken);
            return;
        }

        if (result.Success)
        {
            // Update stock level in Redis cache
            await cacheService.SetStockLevelAsync(orderEvent.ProductId, result.NewStock, cancellationToken);

            // Publish inventory-updated (OrderService consumes this)
            await kafkaProducer.PublishInventoryUpdatedAsync(
                new InventoryUpdatedEvent(
                    orderEvent.OrderId, orderEvent.ProductId,
                    "Confirmed", true, result.NewStock, DateTime.UtcNow),
                cancellationToken);

            // Check threshold crossing: only alert when stock drops below threshold
            if (result.NewStock < _lowStockThreshold && result.PreviousStock >= _lowStockThreshold)
            {
                _logger.LogWarning(
                    "Product {ProductId} ({ProductName}) crossed low stock threshold: {PreviousStock} → {NewStock}",
                    orderEvent.ProductId, result.ProductName, result.PreviousStock, result.NewStock);

                await kafkaProducer.PublishLowStockAlertAsync(
                    new LowStockAlertEvent(
                        orderEvent.ProductId, result.ProductName,
                        result.NewStock, _lowStockThreshold, DateTime.UtcNow),
                    cancellationToken);
            }

            _logger.LogInformation(
                "Order {OrderId} confirmed — product {ProductId} stock: {NewStock}",
                orderEvent.OrderId, orderEvent.ProductId, result.NewStock);
        }
        else
        {
            var status = result.WasProductNotFound ? "ProductNotFound" : "OutOfStock";

            await kafkaProducer.PublishInventoryUpdatedAsync(
                new InventoryUpdatedEvent(
                    orderEvent.OrderId, orderEvent.ProductId,
                    status, false, 0, DateTime.UtcNow),
                cancellationToken);

            _logger.LogWarning(
                "Order {OrderId} rejected — {Status} for product {ProductId}",
                orderEvent.OrderId, status, orderEvent.ProductId);
        }
    }
}
