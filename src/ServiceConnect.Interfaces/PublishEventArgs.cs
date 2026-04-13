namespace ServiceConnect.Interfaces;

public sealed class PublishEventArgs : OutgoingEventArgs
{
    public string RoutingKey { get; init; } = string.Empty;
}