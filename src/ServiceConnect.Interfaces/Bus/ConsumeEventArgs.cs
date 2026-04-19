namespace ServiceConnect.Interfaces;

/// <summary>
/// Carries the raw message data received by the telemetry consume pipeline.
/// </summary>
public sealed class ConsumeEventArgs
{
    /// <summary>
    /// Gets the raw message body bytes.
    /// </summary>
    public byte[] Message { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Gets the message type name taken from transport headers.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Gets the transport headers associated with the consumed message.
    /// </summary>
    public IDictionary<string, object> Headers
    {
        // Lazy getter: backing field is null! when ConsumeEventArgs is constructed without
        // setting Headers — avoids the wasted allocation from the field initializer.
        get => _headers ??= new Dictionary<string, object>();
        init => _headers = value ?? new Dictionary<string, object>();
    }

    private IDictionary<string, object> _headers = null!;
}
