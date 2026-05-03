using Microsoft.Extensions.Logging;
using ServiceConnect.Examples.CustomFilterAndMiddleware.Contracts;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CustomFilterAndMiddleware.Consumer;

public sealed class OrderPlacedHandler(ILogger<OrderPlacedHandler> logger) : IMessageHandler<OrderPlaced>
{
    private static int _attemptsForCrashOrder;

    public Task HandleAsync(OrderPlaced message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        // Demonstrate scenario 2 (handler-crash → broker redelivery → dedup filter does NOT block).
        // The first time we see "crash-once", throw — the broker redelivers and the second attempt succeeds.
        if (message.OrderId == "crash-once" && Interlocked.Exchange(ref _attemptsForCrashOrder, 1) == 0)
        {
            logger.LogWarning("Throwing on first delivery of crash-once to demonstrate redelivery handling");
            throw new InvalidOperationException("simulated handler crash");
        }

        logger.LogInformation("Handled OrderPlaced {OrderId} (amount {Amount:C})", message.OrderId, message.Amount);
        return Task.CompletedTask;
    }
}
