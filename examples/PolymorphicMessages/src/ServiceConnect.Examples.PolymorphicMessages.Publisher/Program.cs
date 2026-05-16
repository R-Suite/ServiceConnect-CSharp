using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.PolymorphicMessages.Contracts;
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

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>([]);
services.AddExampleBus(settings, "polymorphic-messages-publisher");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

// Same correlation id on both events so downstream audit logs can tie the
// OrderPlaced and its later OrderShipped back to one conversation. This is the
// idiom documented in learn/core-concepts/messages.mdx.
var correlationId = Guid.NewGuid();
const string orderId = "order-42";

await bus.PublishAsync(new OrderPlaced(correlationId)
{
    OrderId = orderId,
    Total = 129.99m,
});
ConsoleStatus.Success("polymorphic-messages-publisher", $"published order-placed {orderId}");

await bus.PublishAsync(new OrderShipped(correlationId)
{
    OrderId = orderId,
    Carrier = "UPS",
});
ConsoleStatus.Success("polymorphic-messages-publisher", $"published order-shipped {orderId}");
await Console.Out.FlushAsync();
