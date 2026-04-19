namespace ServiceConnect.Telemetry;

/// <summary>
/// RabbitMQ-specific semantic-convention values used by telemetry spans.
/// </summary>
public sealed class RabbitMqMessagingSystemAttributes : IMessagingSystemAttributes
{
    /// <inheritdoc />
    public string MessagingSystem => "rabbitmq";

    /// <inheritdoc />
    public string ProtocolName => "amqp";
}
