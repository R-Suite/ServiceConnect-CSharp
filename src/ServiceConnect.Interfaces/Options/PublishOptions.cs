namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Optional settings for publish operations. Declared as a <c>readonly record struct</c>
/// for parity with <see cref="SendOptions"/> — the mutable sealed-class form allowed
/// concurrent <c>PublishAsync</c> callers sharing one instance to clobber each other
/// between construction and the async pipeline's header read.
/// </summary>
public readonly record struct PublishOptions
{
    /// <summary>
    /// Gets the additional headers to attach to the published message.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Gets the routing key used by the transport, when applicable.
    /// </summary>
    public string? RoutingKey { get; init; }
}
