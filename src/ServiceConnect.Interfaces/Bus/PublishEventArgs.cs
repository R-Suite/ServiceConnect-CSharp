namespace ServiceConnect.Interfaces;

/// <summary>
/// Outgoing telemetry payload for published messages.
/// </summary>
public sealed class PublishEventArgs : OutgoingEventArgs
{
    /// <summary>
    /// Gets the routing key used when publishing the message.
    /// </summary>
    public string RoutingKey { get; init; } = string.Empty;
}
