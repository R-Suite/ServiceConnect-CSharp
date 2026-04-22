using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.MessageDeduplication.Contracts;

public sealed class OrderPlaced(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public decimal Total { get; init; }
}
