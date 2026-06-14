using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ProcessManager.Contracts;

public sealed class PaymentCaptured(Guid correlationId) : Message(correlationId)
{
    public string OrderNumber { get; init; } = string.Empty;
}
