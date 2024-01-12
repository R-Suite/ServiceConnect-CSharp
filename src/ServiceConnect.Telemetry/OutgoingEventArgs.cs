using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

public class OutgoingEventArgs
{
    public Message? Message { get; init; }

    public Dictionary<string, string> Headers
    {
        get => _headers;
        set => _headers = value is not null ? value : new();
    }

    private Dictionary<string, string> _headers = new();
}