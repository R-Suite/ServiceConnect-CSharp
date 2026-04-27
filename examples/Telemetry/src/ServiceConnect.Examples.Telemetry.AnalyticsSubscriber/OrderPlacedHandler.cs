using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Telemetry.AnalyticsSubscriber;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"ANALYTICS:received:{message.OrderId}");
        return Task.CompletedTask;
    }
}
