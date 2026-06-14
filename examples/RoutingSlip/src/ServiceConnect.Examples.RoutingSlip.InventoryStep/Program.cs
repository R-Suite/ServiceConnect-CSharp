using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.RoutingSlip.Contracts;
using ServiceConnect.Examples.RoutingSlip.InventoryStep;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var inventoryQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_INVENTORY_QUEUE_NAME") ?? "routing-slip-inventory";
var billingQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_BILLING_QUEUE_NAME") ?? "routing-slip-billing";
var shippingQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_SHIPPING_QUEUE_NAME") ?? "routing-slip-shipping";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(RoutingSlipOrderHandler), MessageType = typeof(RoutingSlipOrder) }
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<RoutingSlipOrder>, RoutingSlipOrderHandler>();
services.AddExampleBus(
    settings,
    inventoryQueueName,
    configureQueues: queues => queues.AddQueueMapping(typeof(RoutingSlipOrder), [billingQueueName, shippingQueueName]));

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("inventory-step");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
