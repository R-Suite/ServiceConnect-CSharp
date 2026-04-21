using ServiceConnect.Examples.RoutingSlip.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.RoutingSlip.InventoryStep;

public sealed class RoutingSlipOrderHandler : IMessageHandler<RoutingSlipOrder>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(RoutingSlipOrder message)
    {
        message.CurrentStep = "InventoryStep";
        ConsoleStatus.Success("inventory-step", $"processed {message.OrderId} at {message.CurrentStep}");
        await Console.Out.FlushAsync();
    }
}
