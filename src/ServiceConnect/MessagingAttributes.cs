namespace ServiceConnect;

public static class MessagingAttributes
{
    // These constants are defined in the OpenTelemetry specification:
    // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
    public const string MessageId = "messaging.message.id";

    public const string MessageConversationId = "messaging.message.conversation_id";
    public const string MessagingOperation = "messaging.operation";
    public const string MessagingSystem = "messaging.system";
    public const string MessagingDestination = "messaging.destination.name";
    public const string MessagingDestinationAnonymous = "messaging.destination.anonymous";
    public const string MessagingDestinationRoutingKey = "messaging.rabbitmq.destination.routing_key";
    public const string MessagingBodySize = "messaging.message.body.size";
    public const string ProtocolName = "network.protocol.name";
}