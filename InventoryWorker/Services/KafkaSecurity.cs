using Confluent.Kafka;

namespace InventoryWorker.Services;

/// <summary>
/// Applies optional SASL/SSL security settings (e.g. Confluent Cloud) to any Kafka
/// client config. When <c>Kafka:SecurityProtocol</c> is not set, the connection
/// stays plaintext so local development against a self-hosted broker keeps working.
/// </summary>
public static class KafkaSecurity
{
    public static void Apply(ClientConfig config, IConfiguration configuration)
    {
        var securityProtocol = configuration["Kafka:SecurityProtocol"];
        if (string.IsNullOrWhiteSpace(securityProtocol))
        {
            return; // local plaintext broker
        }

        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(securityProtocol, ignoreCase: true);
        config.SaslMechanism = Enum.Parse<SaslMechanism>(
            configuration["Kafka:SaslMechanism"] ?? "Plain", ignoreCase: true);
        config.SaslUsername = configuration["Kafka:SaslUsername"];
        config.SaslPassword = configuration["Kafka:SaslPassword"];
    }
}
