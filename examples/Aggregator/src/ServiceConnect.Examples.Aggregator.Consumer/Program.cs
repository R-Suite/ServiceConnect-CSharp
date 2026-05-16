using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Aggregator.Consumer;
using ServiceConnect.Examples.Aggregator.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "aggregator-consumer";
var databaseName = Environment.GetEnvironmentVariable("SC_EXAMPLES_DATABASE_NAME") ?? "aggregator_consumer";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

await DependencyWaiter.WaitForMongoDbAsync(
    settings.MongoConnectionString,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(TelemetrySliceAggregator), MessageType = typeof(TelemetrySlice) }
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddTransient<Aggregator<TelemetrySlice>, TelemetrySliceAggregator>();
services.AddExampleBus(settings, queueName, useMongoDb: true, databaseName: databaseName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("aggregator-consumer");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
