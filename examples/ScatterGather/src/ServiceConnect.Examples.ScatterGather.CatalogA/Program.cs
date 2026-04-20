using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.ScatterGather.CatalogA;
using ServiceConnect.Examples.ScatterGather.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_CATALOG_A_QUEUE_NAME") ?? "scatter-gather-catalog-a";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(SearchRequestHandler), MessageType = typeof(SearchRequest) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<SearchRequest>, SearchRequestHandler>();
services.AddExampleBus(settings, queueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("scatter-gather-catalog-a");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
