namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Optional settings for send operations.
/// </summary>
public readonly record struct SendOptions
{
    /// <summary>
    /// Gets the additional headers to attach to the message.
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Gets the single destination endpoint.
    /// </summary>
    public string? EndPoint { get; init; }

    /// <summary>
    /// Gets the destination endpoints when sending to multiple queues.
    /// </summary>
    public IList<string>? EndPoints { get; init; }
}
