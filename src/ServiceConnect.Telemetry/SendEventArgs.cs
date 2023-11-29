using ServiceConnect.Interfaces;

namespace ServiceConnect.Telemetry;

public class SendEventArgs
{
    public string EndPoint { get; init; }

    public IList<string> EndPoints
    {
        get => EndPoint
                .Remove(0)
                .Remove(EndPoint.Length - 1)
                .Split(',');
        init => EndPoint = "[" + string.Join(',', value) + "]";
    }

    public Message Message { get; init; }

    public Dictionary<string, string> Headers
    {
        get => _headers;
        init => _headers = value is not null ? value : new();
    }

    private Dictionary<string, string> _headers;
}