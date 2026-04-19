namespace ServiceConnect.Telemetry;

/// <summary>
/// OpenTelemetry semantic-convention attribute names used by ServiceConnect spans.
/// </summary>
public static class MessagingAttributes
{
    // These constants are defined in the OpenTelemetry specification:
    // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
    /// <summary>
    /// Attribute name for the logical message identifier.
    /// </summary>
    public const string MessageId = "messaging.message.id";

    /// <summary>
    /// Attribute name for the conversation or correlation identifier.
    /// </summary>
    public const string MessageConversationId = "messaging.message.conversation_id";
    /// <summary>
    /// Attribute name for the messaging operation name.
    /// </summary>
    public const string MessagingOperation = "messaging.operation";
    /// <summary>
    /// Attribute name for the messaging system identifier.
    /// </summary>
    public const string MessagingSystem = "messaging.system";
    /// <summary>
    /// Attribute name for the destination queue, topic, or exchange name.
    /// </summary>
    public const string MessagingDestination = "messaging.destination.name";
    /// <summary>
    /// Attribute name used when the destination is anonymous or implicit.
    /// </summary>
    public const string MessagingDestinationAnonymous = "messaging.destination.anonymous";
    /// <summary>
    /// Attribute name for the RabbitMQ routing key.
    /// </summary>
    public const string MessagingDestinationRoutingKey = "messaging.rabbitmq.destination.routing_key";
    /// <summary>
    /// Attribute name for the serialized body size in bytes.
    /// </summary>
    public const string MessagingBodySize = "messaging.message.body.size";
    /// <summary>
    /// Attribute name for the network protocol name.
    /// </summary>
    public const string ProtocolName = "network.protocol.name";
}
