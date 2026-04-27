using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Examples.Telemetry.Contracts;
using ServiceConnect.Examples.Telemetry.Publisher;
using ServiceConnect.Interfaces;
using ServiceConnect.Telemetry;

TelemetryConsoleListener.Register("telemetry-publisher");

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "telemetry-publisher",
    configureBuilder: builder => builder.AddTelemetry());

// To export to a real OTel pipeline, replace the listener registration above with:
// services.AddOpenTelemetry().WithTracing(t => t
//     .AddSource(ServiceConnectActivitySource.PublishActivitySourceName)
//     .AddSource(ServiceConnectActivitySource.SendActivitySourceName)
//     .AddSource(ServiceConnectActivitySource.ConsumeActivitySourceName)
//     .AddConsoleExporter());

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

ConsoleStatus.Ready("telemetry-publisher");

await bus.PublishAsync(new OrderPlaced(Guid.NewGuid())
{
    OrderId = Guid.NewGuid().ToString(),
    Total = 42.50m,
});
ConsoleStatus.Success("telemetry-publisher", "published order");
