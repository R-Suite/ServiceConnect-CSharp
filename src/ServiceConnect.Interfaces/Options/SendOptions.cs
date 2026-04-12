namespace ServiceConnect.Interfaces.Options;

public sealed class SendOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
}
