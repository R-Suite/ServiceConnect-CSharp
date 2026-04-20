using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ContentBasedRouting.Contracts;

public sealed class PremiumOrderPlaced(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
}
