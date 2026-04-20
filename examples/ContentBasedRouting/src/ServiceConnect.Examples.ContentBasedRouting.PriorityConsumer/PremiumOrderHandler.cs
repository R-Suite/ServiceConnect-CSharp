using ServiceConnect.Examples.ContentBasedRouting.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.ContentBasedRouting.PriorityConsumer;

public sealed class PremiumOrderHandler : IMessageHandler<PremiumOrderPlaced>
{
    public IConsumeContext? Context { get; set; }

    public async Task HandleAsync(PremiumOrderPlaced message)
    {
        ConsoleStatus.Success("priority-consumer", $"processed {message.OrderId}");
        await Console.Out.FlushAsync();
    }
}
