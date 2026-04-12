namespace ServiceConnect.Interfaces;

public sealed class HandlerReference
{
    public required Type MessageType { get; set; }
    public required Type HandlerType { get; set; }
    public IList<string> RoutingKeys { get; set; } = [];
}
