namespace ServiceConnect.Interfaces;

/// <summary>
/// Base event payload for outgoing publish and send telemetry.
/// </summary>
public class OutgoingEventArgs
{
    /// <summary>
    /// Gets the outgoing message instance, when available.
    /// </summary>
    public Message? Message { get; init; }

    /// <summary>
    /// Gets or sets the outgoing transport headers.
    /// </summary>
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
