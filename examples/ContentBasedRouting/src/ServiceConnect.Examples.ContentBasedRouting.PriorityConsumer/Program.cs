using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ContentBasedRouting.Contracts;
using ServiceConnect.Examples.ContentBasedRouting.PriorityConsumer;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_PRIORITY_QUEUE_NAME") ?? "priority-consumer";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(PremiumOrderHandler), MessageType = typeof(PremiumOrderPlaced) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<PremiumOrderPlaced>, PremiumOrderHandler>();
services.AddExampleBus(settings, queueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("priority-consumer");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
