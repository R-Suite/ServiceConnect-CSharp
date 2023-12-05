namespace ServiceConnect.Telemetry;

public class SendEventArgs : OutgoingEventArgs
{
    public string EndPoint { get; init; } = string.Empty;

    public IList<string> EndPoints
    {
        get => EndPoint
                .Remove(0)
                .Remove(EndPoint.Length - 1)
                .Split(',');
        init => EndPoint = "[" + string.Join(',', value) + "]";
    }
}