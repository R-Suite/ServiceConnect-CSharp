using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.ProcessManager.PaymentWorker;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var paymentQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_PAYMENT_QUEUE_NAME") ?? "process-manager-payment";
var workflowQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_WORKFLOW_QUEUE_NAME") ?? "process-manager-orchestrator";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(InventoryReservedHandler), MessageType = typeof(InventoryReserved) }
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddSingleton(new WorkflowQueue(workflowQueueName));
services.AddTransient<IMessageHandler<InventoryReserved>, InventoryReservedHandler>();
services.AddExampleBus(settings, paymentQueueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("payment-worker");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
