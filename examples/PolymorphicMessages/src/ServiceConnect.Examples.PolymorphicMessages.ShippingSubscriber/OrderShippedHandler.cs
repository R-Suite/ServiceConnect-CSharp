using ServiceConnect.Examples.PolymorphicMessages.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PolymorphicMessages.ShippingSubscriber;

public sealed class OrderShippedHandler : IMessageHandler<OrderShipped>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderShipped message, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("shipping-subscriber", $"processed order-shipped {message.OrderId}");
        return Task.CompletedTask;
    }
}
