using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.RoutingSlip.Contracts;
using ServiceConnect.Examples.RoutingSlip.ShippingStep;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
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
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<RoutingSlipOrder>, RoutingSlipOrderHandler>();
services.AddExampleBus(settings, shippingQueueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("shipping-step");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
