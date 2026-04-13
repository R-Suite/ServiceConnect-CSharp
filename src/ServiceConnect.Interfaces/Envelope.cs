namespace ServiceConnect.Interfaces;

public sealed class Envelope
{
    /// <summary>Headers accumulated by the pipeline. Mutable by filters but the dictionary reference is fixed.</summary>
    public IDictionary<string, object> Headers { get; init; } = new Dictionary<string, object>();
    /// <summary>Message body bytes. Set once at construction.</summary>
    public byte[] Body { get; init; } = Array.Empty<byte>();
}
