using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.PointToPoint.Consumer;
using ServiceConnect.Examples.PointToPoint.Contracts;
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
    new() { HandlerType = typeof(WorkSubmittedHandler), MessageType = typeof(WorkSubmitted) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<WorkSubmitted>, WorkSubmittedHandler>();
services.AddExampleBus(settings, "point-to-point-consumer");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("point-to-point-consumer");
await Task.Delay(Timeout.InfiniteTimeSpan);
