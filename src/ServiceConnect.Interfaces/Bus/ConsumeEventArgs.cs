namespace ServiceConnect.Interfaces;

/// <summary>
/// Carries the raw message data received by the telemetry consume pipeline.
/// </summary>
public sealed class ConsumeEventArgs
{
    /// <summary>
    /// Gets the raw message body bytes.
    /// </summary>
    public byte[] Message { get; init; } = [];

    /// <summary>
    /// Gets the message type name taken from transport headers.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Gets the transport headers associated with the consumed message.
    /// </summary>
    public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>();
}
