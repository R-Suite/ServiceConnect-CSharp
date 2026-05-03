namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents a transport envelope containing headers and a raw body payload.
/// </summary>
public sealed class Envelope
{
    /// <summary>
    /// Mutable transport headers for this envelope. The framework writes pipeline-managed
    /// headers (TimeProcessed, DestinationAddress, etc.) here between deserialization and
    /// handler dispatch, so the dictionary must be mutable. User code that reads via
    /// <see cref="IConsumeContext.Headers"/> sees a read-only projection over this same
    /// underlying state.
    /// </summary>
    public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
    /// <summary>Message body bytes. Set once at construction.</summary>
    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;
}
