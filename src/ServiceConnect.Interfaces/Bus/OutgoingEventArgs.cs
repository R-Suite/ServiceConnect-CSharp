namespace ServiceConnect.Interfaces;

/// <summary>
/// Base event payload for outgoing publish and send telemetry.
/// </summary>
/// <remarks>
/// Abstract; consumers receive instances of <see cref="PublishEventArgs"/> or
/// <see cref="SendEventArgs"/>. Future versions may add required members to this base
/// class — subclassing is reserved to the framework so consumers are not broken by
/// a future minor-version addition.
/// </remarks>
public abstract class OutgoingEventArgs
{
    /// <summary>Initialises a new instance of the <see cref="OutgoingEventArgs"/> class.</summary>
    protected OutgoingEventArgs() { }
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
    public IDictionary<string, string> Headers
    {
        get => _headers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _headers = value;
        }
    }

    private readonly IDictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.Ordinal);
}
