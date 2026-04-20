using ServiceConnect.Examples.PublishSubscribe.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PublishSubscribe.BillingSubscriber;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public IConsumeContext? Context { get; set; }

    public Task HandleAsync(OrderPlaced message)
    {
        ConsoleStatus.Success("billing-subscriber", $"processed {message.OrderId}");
        return Task.CompletedTask;
    }
}
