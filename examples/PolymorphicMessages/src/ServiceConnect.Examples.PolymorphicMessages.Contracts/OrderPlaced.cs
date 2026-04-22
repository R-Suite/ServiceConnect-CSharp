namespace ServiceConnect.Examples.PolymorphicMessages.Contracts;

public sealed class OrderPlaced(Guid correlationId) : DomainEvent(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public decimal Total { get; init; }
}
