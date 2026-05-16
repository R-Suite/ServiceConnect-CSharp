namespace ServiceConnect.Interfaces;

/// <summary>
/// Outgoing telemetry payload for point-to-point sends. For multi-endpoint fan-out
/// (<c>IBus.SendToManyAsync</c>), one <see cref="SendEventArgs"/> is raised per
/// destination — each with its own <see cref="EndPoint"/>. Subscribers that need to
/// see the full fan-out should correlate by message <c>CorrelationId</c>, which is
/// stable across the per-endpoint deliveries.
/// </summary>
public sealed class SendEventArgs : OutgoingEventArgs
{
    /// <summary>
    /// Gets the destination endpoint for this delivery.
    /// </summary>
    public string EndPoint { get; init; } = string.Empty;
}
