using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.ProcessManager.PaymentWorker;

public sealed record WorkflowQueue(string Name);

public sealed class InventoryReservedHandler(WorkflowQueue workflowQueue) : IMessageHandler<InventoryReserved>
{
    public async Task HandleAsync(InventoryReserved message, IConsumeContext context, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("payment-worker", $"captured payment for {message.CorrelationId}");
        await Console.Out.FlushAsync();

        await context.Bus.SendAsync(
            new PaymentCaptured(message.CorrelationId) { OrderNumber = message.OrderNumber },
            new SendOptions { EndPoint = workflowQueue.Name },
            context.CancellationToken);
    }
}
