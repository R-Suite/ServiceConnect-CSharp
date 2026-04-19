namespace ServiceConnect.Interfaces.Options;

/// <summary>
/// Optional settings for publish operations.
/// </summary>
public sealed class PublishOptions
{
    /// <summary>
    /// Gets or sets additional headers to attach to the published message.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    /// Gets or sets the routing key used by the transport, when applicable.
    /// </summary>
    public string? RoutingKey { get; set; }
}
