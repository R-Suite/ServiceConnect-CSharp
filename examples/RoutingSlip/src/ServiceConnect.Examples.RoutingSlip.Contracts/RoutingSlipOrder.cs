using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.RoutingSlip.Contracts;

public sealed class RoutingSlipOrder(Guid correlationId) : Message(correlationId)
{
    public string OrderId { get; init; } = string.Empty;
    public string CurrentStep { get; set; } = string.Empty;
}
