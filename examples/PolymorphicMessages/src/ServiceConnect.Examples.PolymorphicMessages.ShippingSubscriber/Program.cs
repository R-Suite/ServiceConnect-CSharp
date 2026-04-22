using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.PolymorphicMessages.Contracts;
using ServiceConnect.Examples.PolymorphicMessages.ShippingSubscriber;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(OrderShippedHandler), MessageType = typeof(OrderShipped) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<OrderShipped>, OrderShippedHandler>();
services.AddExampleBus(settings, "shipping-subscriber");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("shipping-subscriber");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
