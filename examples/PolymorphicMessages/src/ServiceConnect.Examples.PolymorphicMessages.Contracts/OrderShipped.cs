namespace ServiceConnect.Examples.PolymorphicMessages.Contracts;

public sealed class OrderShipped(Guid correlationId) : DomainEvent(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public string Carrier { get; init; } = string.Empty;
}
