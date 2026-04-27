using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Examples.Telemetry.AnalyticsSubscriber;
using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;

TelemetryConsoleListener.Register("analytics-subscriber");

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(OrderPlacedHandler), MessageType = typeof(OrderPlaced) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<OrderPlaced>, OrderPlacedHandler>();
services.AddExampleBus(settings, "analytics-subscriber",
    configureBuilder: builder => builder.AddTelemetry());

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("analytics-subscriber");
await Task.Delay(Timeout.InfiniteTimeSpan);
