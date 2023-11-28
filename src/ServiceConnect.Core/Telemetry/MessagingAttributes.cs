namespace ServiceConnect.Core.Telemetry;

internal static class MessagingAttributes
{
    // These constants are defined in the OpenTelemetry specification:
    // https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#messaging-attributes
    internal const string MessageId = "messaging.message.id";

    internal const string MessageConversationId = "messaging.message.conversation_id";
    internal const string MessagingOperation = "messaging.operation";
    internal const string MessagingSystem = "messaging.system";
    internal const string MessagingDestination = "messaging.destination.name";
    internal const string MessagingDestinationAnonymous = "messaging.destination.anonymous";
    internal const string MessagingDestinationRoutingKey = "messaging.rabbitmq.destination.routing_key";
    internal const string MessagingBodySize = "messaging.message.body.size";
    internal const string ProtocolName = "network.protocol.name";
}