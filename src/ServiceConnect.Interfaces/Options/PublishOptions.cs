namespace ServiceConnect.Interfaces.Options;

public sealed class PublishOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? RoutingKey { get; set; }
}
