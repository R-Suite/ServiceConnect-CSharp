using ServiceConnect.Examples.ContentBasedRouting.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ContentBasedRouting.StandardConsumer;

public sealed class StandardOrderHandler : IMessageHandler<StandardOrderPlaced>
{
    public async Task HandleAsync(StandardOrderPlaced message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("standard-consumer", $"processed {message.OrderId}");
        await Console.Out.FlushAsync();
    }
}
