namespace ServiceConnect.Interfaces;

public sealed class HandlerReference
{
    public required Type MessageType { get; init; }
    public required Type HandlerType { get; init; }
}
