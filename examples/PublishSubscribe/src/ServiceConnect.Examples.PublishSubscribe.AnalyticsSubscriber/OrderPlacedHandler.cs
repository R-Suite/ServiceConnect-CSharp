using ServiceConnect.Examples.PublishSubscribe.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PublishSubscribe.AnalyticsSubscriber;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("analytics-subscriber", $"processed {message.OrderId}");
        return Task.CompletedTask;
    }
}
