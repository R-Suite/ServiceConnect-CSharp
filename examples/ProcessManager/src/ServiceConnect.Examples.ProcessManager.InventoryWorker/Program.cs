using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.ProcessManager.InventoryWorker;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var inventoryQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_INVENTORY_QUEUE_NAME") ?? "process-manager-inventory";
var workflowQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_WORKFLOW_QUEUE_NAME") ?? "process-manager-orchestrator";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(OrderSubmittedHandler), MessageType = typeof(OrderSubmitted) }
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddSingleton(new WorkflowQueue(workflowQueueName));
services.AddTransient<IMessageHandler<OrderSubmitted>, OrderSubmittedHandler>();
services.AddExampleBus(settings, inventoryQueueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("inventory-worker");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
