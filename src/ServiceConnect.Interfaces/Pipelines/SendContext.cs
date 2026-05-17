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

    /// <summary>
    /// The serialized message body, exactly as the producer will send it. Read-only:
    /// <see cref="ISendMessageMiddleware"/> implementations CANNOT rewrite the wire payload
    /// here (the property is <c>init</c>-only). Use cases like compression, encryption, or
    /// signing of the body must be applied at the <see cref="IMessageSerializer"/> layer
    /// (or via a custom serializer) — not in send-pipeline middleware. Middleware can still
    /// inspect the bytes for observability (size, content-type sniffing) and mutate
    /// <see cref="Headers"/> (tracing, signing-hash headers, dedup keys).
    /// </summary>
    public required ReadOnlyMemory<byte> MessageBytes { get; init; }

    /// <summary>
    /// Mutable transport headers for the outgoing message. Pipeline middleware (telemetry,
    /// signing-hash stamping, dedup-key writing) mutates this dictionary before the message
    /// is published. Distinct from <see cref="ConsumeEventArgs.Headers"/> (read-only —
    /// incoming side) and from <see cref="OutgoingEventArgs.Headers"/> (also mutable,
    /// observed by telemetry).
    /// </summary>
    public required IDictionary<string, string> Headers { get; init; }

    /// <summary>The destination endpoint when applicable; null for publish.</summary>
    public string? EndPoint { get; init; }

    /// <summary>The routing key when applicable; null otherwise.</summary>
    public string? RoutingKey { get; init; }

    /// <summary>The call site that produced this context.</summary>
    public required SendOperation Operation { get; init; }

    /// <summary>
    /// Framework-controlled outbound routing-slip hop counter. Set by <c>Bus.RouteAsync</c>
    /// before the send middleware runs; stamped onto the outgoing transport headers by the
    /// producer after middleware. <c>ISendMessageMiddleware</c> instances see this value but
    /// cannot affect the wire-header — the producer treats <c>RoutingSlipHopsCompleted</c>
    /// as a reserved key and overwrites any middleware-mutated entry.
    /// </summary>
    public int? RoutingSlipHopsCompleted { get; init; }
}
