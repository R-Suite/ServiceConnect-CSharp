namespace ServiceConnect.Interfaces;

/// <summary>
/// Carries the raw message data received by the telemetry consume pipeline.
/// </summary>
public sealed class ConsumeEventArgs
{
    /// <summary>
    /// Gets the raw message body bytes. Populated lazily — the consume middleware
    /// only materialises the array when an enricher is configured. Use
    /// <see cref="BodySize"/> for the on-wire byte count regardless of whether the
    /// bytes themselves were materialised.
    /// </summary>
    public byte[] Message { get; init; } = [];

    /// <summary>
    /// Gets the on-wire body length in bytes. Always populated by the consume middleware,
    /// even when <see cref="Message"/> is the empty sentinel array because no enricher
    /// requested the materialised bytes. Used to stamp the OTel
    /// <c>messaging.message.body.size</c> attribute correctly on every consume span.
    /// </summary>
    public int BodySize { get; init; }

    /// <summary>
    /// Gets the message type name taken from transport headers.
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// Gets the transport headers associated with the consumed message.
    /// </summary>
    public IReadOnlyDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
}
