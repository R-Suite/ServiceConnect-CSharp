using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.ProcessManager.InventoryWorker;

public sealed record WorkflowQueue(string Name);

public sealed class OrderSubmittedHandler(WorkflowQueue workflowQueue) : IMessageHandler<OrderSubmitted>
{
    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(OrderSubmitted message, CancellationToken cancellationToken = default)
    {
        ConsoleStatus.Success("inventory-worker", $"reserved inventory for {message.CorrelationId}");
        await Console.Out.FlushAsync();

        await Context!.Bus.SendAsync(
            new InventoryReserved(message.CorrelationId) { OrderNumber = message.OrderNumber },
            new SendOptions { EndPoint = workflowQueue.Name },
            Context.CancellationToken);
    }
}
