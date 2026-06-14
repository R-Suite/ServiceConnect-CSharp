using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ProcessManager.Contracts;

public sealed class FulfillmentState : IProcessManagerData
{
    public Guid CorrelationId { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public bool IsSubmitted { get; set; }

    public bool InventoryReserved { get; set; }

    public bool PaymentCaptured { get; set; }

    public bool IsCompleted { get; set; }
}
