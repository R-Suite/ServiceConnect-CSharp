namespace ServiceConnect.Interfaces;

public class RequestOptions
{
    public Dictionary<string, string>? Headers { get; set; }
    public string? EndPoint { get; set; }
    public IList<string>? EndPoints { get; set; }
    public int Timeout { get; set; } = 10000;
    public int? ExpectedReplyCount { get; set; }
}
