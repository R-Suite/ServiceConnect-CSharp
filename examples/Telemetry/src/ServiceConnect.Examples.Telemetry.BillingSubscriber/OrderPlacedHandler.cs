using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Telemetry.BillingSubscriber;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"BILLING:received:{message.OrderId}");
        return Task.CompletedTask;
    }
}
