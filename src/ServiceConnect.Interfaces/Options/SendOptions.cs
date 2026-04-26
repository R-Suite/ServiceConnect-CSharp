namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Optional settings for send operations.
/// </summary>
public readonly record struct SendOptions
{
    /// <summary>
    /// Gets the additional headers to attach to the message. Typed as a read-only
    /// view so the framework does not invite concurrent-caller mutation of a
    /// shared dictionary while the async pipeline is iterating it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Gets the single destination endpoint.
    /// </summary>
    public string? EndPoint { get; init; }

    /// <summary>
    /// Gets the destination endpoints when sending to multiple queues.
    /// </summary>
    public IReadOnlyList<string>? EndPoints { get; init; }
}
