using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.MessageDeduplication.Consumer;
using ServiceConnect.Examples.MessageDeduplication.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "dedup-consumer";
var mongoDbName = Environment.GetEnvironmentVariable("SC_DEDUP_MONGO_DB") ?? "dedup-sample";

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
    new() { HandlerType = typeof(OrderPlacedHandler), MessageType = typeof(OrderPlaced) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<OrderPlaced>, OrderPlacedHandler>();

// Register the filter before AddExampleBus so IncomingDeduplicationFilter
// is resolvable when the bus pipeline wires it in below.
services.AddMessageDeduplicationFilter(options =>
{
    options.PersistorType = PersistorType.MongoDb;
    options.ConnectionStringMongoDb = settings.MongoConnectionString;
    options.DatabaseNameMongoDb = mongoDbName;
    options.CollectionNameMongoDb = "ProcessedMessages";
    options.MsgExpiryHours = 24;
});

services.AddExampleBus(settings, queueName, configureBuilder: builder =>
{
    builder.AddBeforeConsumingFilter<IncomingDeduplicationFilter>();
});

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("dedup-consumer");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
