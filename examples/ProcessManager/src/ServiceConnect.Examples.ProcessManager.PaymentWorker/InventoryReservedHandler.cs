using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.ProcessManager.PaymentWorker;

public sealed record WorkflowQueue(string Name);

public sealed class InventoryReservedHandler(WorkflowQueue workflowQueue) : IMessageHandler<InventoryReserved>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(InventoryReserved message)
    {
        ConsoleStatus.Success("payment-worker", $"captured payment for {message.CorrelationId}");
        await Console.Out.FlushAsync();

        await Context!.Bus.SendAsync(
            new PaymentCaptured(message.CorrelationId) { OrderNumber = message.OrderNumber },
            new SendOptions { EndPoint = workflowQueue.Name },
            Context.CancellationToken);
    }
}
