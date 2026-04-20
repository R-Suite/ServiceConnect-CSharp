using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PublishSubscribe.Contracts;

public sealed class OrderPlaced(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
}
