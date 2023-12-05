namespace ServiceConnect.Telemetry;

public class PublishEventArgs : OutgoingEventArgs
{
    public string RoutingKey { get; init; } = string.Empty;
}