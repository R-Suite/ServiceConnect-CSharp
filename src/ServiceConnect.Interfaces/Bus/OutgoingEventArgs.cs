namespace ServiceConnect.Interfaces;

/// <summary>
/// Base event payload for outgoing publish and send telemetry.
/// </summary>
public class OutgoingEventArgs
{
    /// <summary>
    /// Gets the outgoing message instance, when available.
    /// </summary>
    public Message? Message { get; init; }

    /// <summary>
    /// Gets the outgoing transport headers. <c>init</c>-only so a subscriber can
    /// still mutate individual entries (e.g. a telemetry hook injecting a
    /// <c>traceparent</c>) but cannot swap out the entire dictionary after the
    /// framework has built it. A public setter would let subscribers replace the
    /// map and strip required MessageType/CorrelationId entries before the
    /// transport send.
    /// </summary>
    public Dictionary<string, string> Headers
    {
        get => _headers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _headers = value;
        }
    }

    private readonly Dictionary<string, string> _headers = [];
}
