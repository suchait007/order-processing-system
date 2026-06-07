using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;

namespace OrderService.Services;

public class KafkaConsumerService : BackgroundService
{
    private readonly ConsumerConfig _consumerConfig;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<KafkaConsumerService> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public KafkaConsumerService(
        IConfiguration configuration,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<KafkaConsumerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        var bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka bootstrap servers are not configured.");

        _consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "order-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        KafkaSecurity.Apply(_consumerConfig, configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, string>(_consumerConfig).Build();
        consumer.Subscribe("inventory-updated");

        _logger.LogInformation("Kafka consumer subscribed to inventory-updated topic");

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
                    var inventoryEvent = JsonSerializer.Deserialize<InventoryUpdatedEvent>(consumeResult.Message.Value, SerializerOptions);
                    var newStatus = ResolveStatus(inventoryEvent);

                    if (inventoryEvent is null || inventoryEvent.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(newStatus))
                    {
                        _logger.LogWarning("Skipping invalid inventory update message: {Message}", consumeResult.Message.Value);
                        consumer.Commit(consumeResult);
                        continue;
                    }

                    using var scope = _serviceScopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
                    var cacheService = scope.ServiceProvider.GetRequiredService<RedisCacheService>();

                    var order = await dbContext.Orders.FirstOrDefaultAsync(order => order.Id == inventoryEvent.OrderId, stoppingToken);
                    if (order is null)
                    {
                        _logger.LogWarning("Order {OrderId} not found for inventory update", inventoryEvent.OrderId);
                        consumer.Commit(consumeResult);
                        continue;
                    }

                    order.Status = newStatus;
                    order.UpdatedAt = DateTime.UtcNow;
                    await dbContext.SaveChangesAsync(stoppingToken);
                    await cacheService.SetOrderStatusAsync(order.Id, order.Status, stoppingToken);

                    _logger.LogInformation("Updated order {OrderId} status to {Status} from Kafka message", order.Id, order.Status);
                    consumer.Commit(consumeResult);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process inventory-updated message");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Kafka consumer is stopping");
        }
        finally
        {
            consumer.Close();
        }
    }

    private static string? ResolveStatus(InventoryUpdatedEvent? inventoryEvent)
    {
        if (inventoryEvent is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(inventoryEvent.Status))
        {
            return inventoryEvent.Status;
        }

        if (inventoryEvent.InStock.HasValue)
        {
            return inventoryEvent.InStock.Value ? "Confirmed" : "OutOfStock";
        }

        return null;
    }

    private sealed class InventoryUpdatedEvent
    {
        public Guid OrderId { get; set; }
        public string? Status { get; set; }
        public bool? InStock { get; set; }
    }
}
