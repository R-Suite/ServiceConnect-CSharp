using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.CompetingConsumers.Contracts;
using ServiceConnect.Examples.CompetingConsumers.WorkerA;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "competing-consumers-work";
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(JobQueuedHandler), MessageType = typeof(JobQueued) }
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<JobQueued>, JobQueuedHandler>();
services.AddExampleBus(settings, queueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("worker-a");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
