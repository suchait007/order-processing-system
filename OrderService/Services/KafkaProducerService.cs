using System.Text.Json;
using Confluent.Kafka;
using OrderService.Models;

namespace OrderService.Services;

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
            BootstrapServers = bootstrapServers
        };
        KafkaSecurity.Apply(producerConfig, configuration);

        _producer = new ProducerBuilder<string, string>(producerConfig).Build();
    }

    public async Task PublishOrderPlacedAsync(Order order, CancellationToken cancellationToken = default)
    {
        var payload = new OrderPlacedEvent(order.Id, order.ProductId, order.Quantity, DateTime.UtcNow);
        var message = new Message<string, string>
        {
            Key = order.Id.ToString(),
            Value = JsonSerializer.Serialize(payload, SerializerOptions)
        };

        var result = await _producer.ProduceAsync("order-placed", message, cancellationToken);

        _logger.LogInformation(
            "Published order placed event for OrderId={OrderId} to Kafka topic {Topic} partition {Partition} at offset {Offset}",
            order.Id,
            result.Topic,
            result.Partition.Value,
            result.Offset.Value);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }

    private sealed record OrderPlacedEvent(Guid OrderId, int ProductId, int Quantity, DateTime Timestamp);
}
