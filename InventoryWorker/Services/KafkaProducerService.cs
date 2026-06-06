using System.Text.Json;
using Confluent.Kafka;
using InventoryWorker.Models;

namespace InventoryWorker.Services;

public class KafkaProducerService : IDisposable
{
    private readonly ILogger<KafkaProducerService> _logger;
    private readonly IProducer<string, string> _producer;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public KafkaProducerService(IConfiguration configuration, ILogger<KafkaProducerService> logger)
    {
        _logger = logger;

        var bootstrapServers = configuration["Kafka:BootstrapServers"]
            ?? throw new InvalidOperationException("Kafka bootstrap servers are not configured.");

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            Acks = Acks.All,
            EnableIdempotence = true
        };

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
    }

    public async Task PublishInventoryUpdatedAsync(InventoryUpdatedEvent inventoryEvent, CancellationToken cancellationToken = default)
    {
        var message = new Message<string, string>
        {
            Key = inventoryEvent.OrderId.ToString(),
            Value = JsonSerializer.Serialize(inventoryEvent, SerializerOptions)
        };

        var result = await _producer.ProduceAsync("inventory-updated", message, cancellationToken);

        _logger.LogInformation(
            "Published inventory-updated for OrderId={OrderId} Status={Status} to partition {Partition} offset {Offset}",
            inventoryEvent.OrderId, inventoryEvent.Status,
            result.Partition.Value, result.Offset.Value);
    }

    public async Task PublishLowStockAlertAsync(LowStockAlertEvent alert, CancellationToken cancellationToken = default)
    {
        var message = new Message<string, string>
        {
            Key = alert.ProductId.ToString(),
            Value = JsonSerializer.Serialize(alert, SerializerOptions)
        };

        var result = await _producer.ProduceAsync("low-stock-alert", message, cancellationToken);

        _logger.LogInformation(
            "Published low-stock-alert for ProductId={ProductId} Stock={CurrentStock} Threshold={Threshold} to partition {Partition} offset {Offset}",
            alert.ProductId, alert.CurrentStock, alert.Threshold,
            result.Partition.Value, result.Offset.Value);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
