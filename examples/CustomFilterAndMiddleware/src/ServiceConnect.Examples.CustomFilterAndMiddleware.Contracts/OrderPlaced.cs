using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts;

public sealed class OrderPlaced(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}
