namespace ServiceConnect.Interfaces.Options;

public readonly record struct SendOptions
{
    public Dictionary<string, string>? Headers { get; init; }
    public string? EndPoint { get; init; }
    public IList<string>? EndPoints { get; init; }
}
