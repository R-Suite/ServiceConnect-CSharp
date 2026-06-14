using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ContentBasedRouting.Contracts;
using ServiceConnect.Examples.ContentBasedRouting.StandardConsumer;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_STANDARD_QUEUE_NAME") ?? "standard-consumer";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(StandardOrderHandler), MessageType = typeof(StandardOrderPlaced) }
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<StandardOrderPlaced>, StandardOrderHandler>();
services.AddExampleBus(settings, queueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("standard-consumer");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
