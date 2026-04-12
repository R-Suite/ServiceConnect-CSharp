namespace ServiceConnect.Interfaces;

public class SendEventArgs : OutgoingEventArgs
{
    public string EndPoint { get; init; } = string.Empty;

    public IList<string> EndPoints
    {
        get => EndPoint.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries);
        init => EndPoint = "[" + string.Join(',', value) + "]";
    }
}