using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ProcessManager.Contracts;
using ServiceConnect.Examples.ProcessManager.Orchestrator;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var workflowQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_WORKFLOW_QUEUE_NAME") ?? "process-manager-orchestrator";
var inventoryQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_INVENTORY_QUEUE_NAME") ?? "process-manager-inventory";
var paymentQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_PAYMENT_QUEUE_NAME") ?? "process-manager-payment";
var databaseName = Environment.GetEnvironmentVariable("SC_EXAMPLES_DATABASE_NAME") ?? "process_manager";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

await DependencyWaiter.WaitForMongoDbAsync(
    settings.MongoConnectionString,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(FulfillmentProcessHandler), MessageType = typeof(OrderSubmitted) },
    new() { HandlerType = typeof(FulfillmentProcessHandler), MessageType = typeof(InventoryReserved) },
    new() { HandlerType = typeof(FulfillmentProcessHandler), MessageType = typeof(PaymentCaptured) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddSingleton(new WorkflowQueues(workflowQueueName, inventoryQueueName, paymentQueueName));
services.AddTransient<IProcessHandler<FulfillmentState, OrderSubmitted>, FulfillmentProcessHandler>();
services.AddTransient<IProcessHandler<FulfillmentState, InventoryReserved>, FulfillmentProcessHandler>();
services.AddTransient<IProcessHandler<FulfillmentState, PaymentCaptured>, FulfillmentProcessHandler>();
services.AddExampleBus(settings, workflowQueueName, useMongoDb: true, databaseName: databaseName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("process-manager-orchestrator");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
