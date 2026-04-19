namespace ServiceConnect.Interfaces;

/// <summary>
/// Outgoing telemetry payload for point-to-point sends.
/// </summary>
public sealed class SendEventArgs : OutgoingEventArgs
{
    /// <summary>
    /// Gets the primary destination endpoint.
    /// </summary>
    public string EndPoint { get; init; } = string.Empty;

    /// <summary>
    /// Gets the complete set of destination endpoints when a send targets multiple queues.
    /// </summary>
    public IReadOnlyList<string> EndPoints { get; init; } = [];
}
