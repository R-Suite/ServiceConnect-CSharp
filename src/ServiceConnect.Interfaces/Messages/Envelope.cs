namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents a transport envelope containing headers and a raw body payload.
/// </summary>
public sealed class Envelope
{
    /// <summary>Headers accumulated by the pipeline. Mutable by filters but the dictionary reference is fixed.</summary>
    public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>(StringComparer.Ordinal);
    /// <summary>Message body bytes. Set once at construction.</summary>
    public ReadOnlyMemory<byte> Body { get; init; } = ReadOnlyMemory<byte>.Empty;
}
