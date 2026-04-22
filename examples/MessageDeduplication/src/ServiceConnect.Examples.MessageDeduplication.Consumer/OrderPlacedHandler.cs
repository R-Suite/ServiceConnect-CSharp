using ServiceConnect.Examples.MessageDeduplication.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.MessageDeduplication.Consumer;

public sealed class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(OrderPlaced message)
    {
        ConsoleStatus.Success("dedup-consumer", $"handled {message.OrderId}");

        // Simulate a crash after the side effect. The broker will requeue the
        // message with Redelivered=true; the IncomingDeduplicationFilter blocks
        // that redelivery because the Sender's OutgoingDeduplicationFilter
        // already recorded the MessageId in the shared MongoDB persistor.
        throw new InvalidOperationException(
            "Simulated crash after handling — expect redelivery to be filtered.");
    }
}
