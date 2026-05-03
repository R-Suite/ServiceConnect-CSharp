namespace ServiceConnect.Interfaces;

/// <summary>
/// Carries the data threaded through the outgoing send pipeline so that
/// middleware authors see the strongly-typed <see cref="Message"/> alongside
/// the serialized payload, headers, and routing metadata.
/// </summary>
public sealed class SendContext
{
    /// <summary>The strongly-typed message instance the caller passed.</summary>
    public required Message Message { get; init; }

    /// <summary>The CLR type of <see cref="Message"/>.</summary>
    public required Type MessageType { get; init; }

    /// <summary>The serialized message body, exactly as the producer will send it.</summary>
    public required byte[] MessageBytes { get; init; }

    /// <summary>
    /// Mutable transport headers for the outgoing message. Pipeline middleware (telemetry,
    /// signing, compression, dedup) writes to this dictionary before the message is published.
    /// Distinct from <see cref="ConsumeEventArgs.Headers"/> (read-only — incoming side) and
    /// from <see cref="OutgoingEventArgs.Headers"/> (also mutable, observed by telemetry).
    /// </summary>
    public required IDictionary<string, string> Headers { get; init; }

    /// <summary>The destination endpoint when applicable; null for publish.</summary>
    public string? EndPoint { get; init; }

    /// <summary>The routing key when applicable; null otherwise.</summary>
    public string? RoutingKey { get; init; }

    /// <summary>The call site that produced this context.</summary>
    public required SendOperation Operation { get; init; }
}
