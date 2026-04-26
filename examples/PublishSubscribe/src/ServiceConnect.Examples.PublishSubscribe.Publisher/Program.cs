using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.PublishSubscribe.Contracts;
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
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "publish-subscribe-publisher");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.PublishAsync(new OrderPlaced(Guid.NewGuid()) { OrderId = "order-100" });
ConsoleStatus.Success("publish-subscribe-publisher", "published order-100");
