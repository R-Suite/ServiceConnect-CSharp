namespace ServiceConnect.Interfaces;

public class OutgoingEventArgs
{
    public Message? Message { get; init; }

    public Dictionary<string, string> Headers
    {
        get => _headers;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _headers = value;
        }
    }

    private Dictionary<string, string> _headers = [];
}
