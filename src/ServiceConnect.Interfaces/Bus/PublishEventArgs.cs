namespace ServiceConnect.Interfaces;

/// <summary>
/// Outgoing telemetry payload for published messages.
/// </summary>
public sealed class PublishEventArgs : OutgoingEventArgs
{
    /// <summary>
    /// Gets the broker-side exchange name. For RabbitMQ this is the value stamped onto the
    /// <c>messaging.destination.name</c> OTel attribute (per the messaging semantic conventions).
    /// </summary>
    public string Exchange { get; init; } = string.Empty;

    /// <summary>
    /// Gets the routing key used when publishing the message. Stamped onto the RabbitMQ-specific
    /// <c>messaging.rabbitmq.destination.routing_key</c> OTel attribute when non-empty.
    /// </summary>
    public string RoutingKey { get; init; } = string.Empty;
}
