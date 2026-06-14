namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Optional settings for reply operations sent through <see cref="IConsumeContext.ReplyAsync"/>.
/// Mirrors the shape of <see cref="PublishOptions"/> and <see cref="SendOptions"/> for surface
/// consistency.
/// </summary>
/// <remarks>
/// Carries only <see cref="Headers"/>: replies do not need an endpoint (the destination is the
/// request's reply-to header), do not need a routing key (replies don't fan out), and do not
/// need a correlation id (auto-correlated via the request's <c>MessageId</c>).
/// </remarks>
public readonly record struct ReplyOptions
{
    /// <summary>
    /// Additional headers to attach to the reply message. Read-only view so the framework does
    /// not invite concurrent-caller mutation of a shared dictionary while the async pipeline is
    /// iterating it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
