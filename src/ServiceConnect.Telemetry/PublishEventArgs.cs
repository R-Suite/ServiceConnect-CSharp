using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

public class PublishEventArgs
{
    public Message Message { get; init; }

    public string RoutingKey { get; init; } = string.Empty;

    public Dictionary<string, string> Headers
    {
        get => _headers;
        init => _headers = value is not null ? value : new();
    }

    private Dictionary<string, string> _headers;
}