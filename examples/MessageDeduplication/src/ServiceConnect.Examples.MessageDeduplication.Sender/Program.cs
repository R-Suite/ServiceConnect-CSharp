using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.MessageDeduplication.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Filters.MessageDeduplication;
using ServiceConnect.Filters.MessageDeduplication.Filters;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "dedup-sender";
var mongoDbName = Environment.GetEnvironmentVariable("SC_DEDUP_MONGO_DB") ?? "dedup-sample";
var consumerQueue = Environment.GetEnvironmentVariable("SC_EXAMPLES_CONSUMER_QUEUE") ?? "dedup-consumer";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

await DependencyWaiter.WaitForMongoDbAsync(
    settings.MongoConnectionString,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(new List<HandlerReference>());

// Register the filter before AddExampleBus so OutgoingDeduplicationFilter
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
    builder.AddOutgoingFilter<OutgoingDeduplicationFilter>();
});

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

var orderId = Guid.NewGuid().ToString("N").Substring(0, 8);
var message = new OrderPlaced(Guid.NewGuid())
{
    OrderId = orderId,
    Total = 42.50m
};

await bus.SendAsync(message, new SendOptions { EndPoint = consumerQueue });
ConsoleStatus.Success("dedup-sender", $"sent {orderId}");
await Console.Out.FlushAsync();
