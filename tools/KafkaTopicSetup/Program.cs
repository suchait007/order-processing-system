using Confluent.Kafka;
using Confluent.Kafka.Admin;

// Throwaway admin tool: creates the topics the order-processing system needs
// on Confluent Cloud. Reads connection details from environment variables:
//   KAFKA_BOOTSTRAP, KAFKA_KEY, KAFKA_SECRET
// Usage: dotnet run --project tools/KafkaTopicSetup

string bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP")
    ?? throw new InvalidOperationException("KAFKA_BOOTSTRAP not set");
string apiKey = Environment.GetEnvironmentVariable("KAFKA_KEY")
    ?? throw new InvalidOperationException("KAFKA_KEY not set");
string apiSecret = Environment.GetEnvironmentVariable("KAFKA_SECRET")
    ?? throw new InvalidOperationException("KAFKA_SECRET not set");

// Confluent Cloud requires replication factor 3.
var topics = new[] { "order-placed", "inventory-updated", "low-stock-alert" };

var config = new AdminClientConfig
{
    BootstrapServers = bootstrap,
    SecurityProtocol = SecurityProtocol.SaslSsl,
    SaslMechanism = SaslMechanism.Plain,
    SaslUsername = apiKey,
    SaslPassword = apiSecret
};

using var admin = new AdminClientBuilder(config).Build();

// Discover existing topics so we can skip ones that already exist.
var metadata = admin.GetMetadata(TimeSpan.FromSeconds(30));
var existing = metadata.Topics.Select(t => t.Topic).ToHashSet();
Console.WriteLine($"Connected to {bootstrap}. Existing topics: {existing.Count}");

var toCreate = topics
    .Where(t => !existing.Contains(t))
    .Select(t => new TopicSpecification { Name = t, NumPartitions = 6, ReplicationFactor = 3 })
    .ToList();

foreach (var t in topics.Where(existing.Contains))
{
    Console.WriteLine($"  [skip] '{t}' already exists");
}

if (toCreate.Count == 0)
{
    Console.WriteLine("All topics already present. Nothing to do.");
    return;
}

try
{
    await admin.CreateTopicsAsync(toCreate);
    foreach (var t in toCreate)
    {
        Console.WriteLine($"  [created] '{t.Name}' (partitions={t.NumPartitions}, rf={t.ReplicationFactor})");
    }
    Console.WriteLine("Topic setup complete.");
}
catch (CreateTopicsException ex)
{
    foreach (var r in ex.Results)
    {
        var status = r.Error.IsError ? $"ERROR: {r.Error.Reason}" : "ok";
        Console.WriteLine($"  {r.Topic}: {status}");
    }
    Environment.ExitCode = 1;
}
