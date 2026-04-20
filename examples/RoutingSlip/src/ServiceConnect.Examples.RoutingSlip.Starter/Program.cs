using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.RoutingSlip.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var orderId = Environment.GetEnvironmentVariable("SC_EXAMPLES_ORDER_ID") ?? "routing-slip-order-001";
var inventoryQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_INVENTORY_QUEUE_NAME") ?? "routing-slip-inventory";
var billingQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_BILLING_QUEUE_NAME") ?? "routing-slip-billing";
var shippingQueueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_SHIPPING_QUEUE_NAME") ?? "routing-slip-shipping";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());
services.AddExampleBus(settings, "routing-slip-starter");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.RouteAsync(
    new RoutingSlipOrder(Guid.NewGuid()) { OrderId = orderId, CurrentStep = "InventoryStep" },
    new List<string> { inventoryQueueName, billingQueueName, shippingQueueName });
ConsoleStatus.Success("routing-slip-starter", $"routed {orderId}");
await Console.Out.FlushAsync();
