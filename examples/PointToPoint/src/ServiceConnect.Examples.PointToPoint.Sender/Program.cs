using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.PointToPoint.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "point-to-point-sender");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.SendAsync(
    new WorkSubmitted(Guid.NewGuid()) { WorkId = "work-001" },
    new SendOptions { EndPoint = "point-to-point-consumer" });
ConsoleStatus.Success("point-to-point-sender", "sent work-001");
