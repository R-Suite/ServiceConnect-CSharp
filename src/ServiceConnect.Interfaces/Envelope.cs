namespace ServiceConnect.Interfaces;

public class Envelope
{
    public IDictionary<string, object> Headers { get; set; } = new Dictionary<string, object>();
    public byte[] Body { get; set; } = Array.Empty<byte>();
}
