namespace ServiceConnect.Interfaces.Options;

public sealed class RequestOptions
{
    public const int DefaultTimeoutMs = 10_000;

    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
    public int Timeout { get; set; } = DefaultTimeoutMs;
    public int? ExpectedReplyCount { get; set; }
}
