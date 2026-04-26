using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.ProcessManager.Orchestrator;

public sealed record WorkflowQueues(string WorkflowQueueName, string InventoryQueueName, string PaymentQueueName);

public sealed class FulfillmentProcessHandler(WorkflowQueues queues) :
    IProcessHandler<FulfillmentState, OrderSubmitted>,
    IProcessHandler<FulfillmentState, InventoryReserved>,
    IProcessHandler<FulfillmentState, PaymentCaptured>
{
    private readonly WorkflowQueues _queues = queues;

    public IConsumeContext Context { get; set; } = null!;

    public async Task HandleAsync(OrderSubmitted message, FulfillmentState data, CancellationToken cancellationToken = default)
    {
        if (data.IsSubmitted)
        {
            return;
        }

        data.OrderNumber = message.OrderNumber;
        data.IsSubmitted = true;

        ConsoleStatus.Success("process-manager-orchestrator", $"started workflow {message.CorrelationId}");
        await Console.Out.FlushAsync();

        await Context!.Bus.SendAsync(
            new OrderSubmitted(message.CorrelationId) { OrderNumber = message.OrderNumber },
            new SendOptions { EndPoint = _queues.InventoryQueueName },
            Context.CancellationToken);
    }

    public async Task HandleAsync(InventoryReserved message, FulfillmentState data, CancellationToken cancellationToken = default)
    {
        if (data.InventoryReserved)
        {
            return;
        }

        data.InventoryReserved = true;

        ConsoleStatus.Success("process-manager-orchestrator", $"inventory reserved for {message.CorrelationId}");
        await Console.Out.FlushAsync();

        await Context!.Bus.SendAsync(
            new InventoryReserved(message.CorrelationId) { OrderNumber = message.OrderNumber },
            new SendOptions { EndPoint = _queues.PaymentQueueName },
            Context.CancellationToken);
    }

    public Task HandleAsync(PaymentCaptured message, FulfillmentState data, CancellationToken cancellationToken = default)
    {
        if (data.PaymentCaptured)
        {
            return Task.CompletedTask;
        }

        data.PaymentCaptured = true;
        data.IsCompleted = true;

        ConsoleStatus.Success("process-manager-orchestrator", $"completed workflow {message.CorrelationId}");
        return Console.Out.FlushAsync();
    }
}
