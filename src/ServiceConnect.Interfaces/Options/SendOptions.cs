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
    /// Gets the destination endpoint. Use <see cref="IBus.SendToManyAsync"/> for fan-out
    /// to multiple endpoints.
    /// </summary>
    public string? EndPoint { get; init; }
}
