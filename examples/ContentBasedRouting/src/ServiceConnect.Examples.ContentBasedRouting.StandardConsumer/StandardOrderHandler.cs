using ServiceConnect.Examples.ContentBasedRouting.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ContentBasedRouting.StandardConsumer;

public sealed class StandardOrderHandler : IMessageHandler<StandardOrderPlaced>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(StandardOrderPlaced message)
    {
        ConsoleStatus.Success("standard-consumer", $"processed {message.OrderId}");
        await Console.Out.FlushAsync();
    }
}
