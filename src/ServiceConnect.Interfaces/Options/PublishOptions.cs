namespace ServiceConnect.Interfaces;

public class PublishOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? RoutingKey { get; set; }
}
