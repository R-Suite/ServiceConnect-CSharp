using ServiceConnect.Examples.RoutingSlip.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.RoutingSlip.BillingStep;

public sealed class RoutingSlipOrderHandler : IMessageHandler<RoutingSlipOrder>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(RoutingSlipOrder message, CancellationToken cancellationToken = default)
    {
        message.CurrentStep = "BillingStep";
        ConsoleStatus.Success("billing-step", $"processed {message.OrderId} at {message.CurrentStep}");
        await Console.Out.FlushAsync();
    }
}
