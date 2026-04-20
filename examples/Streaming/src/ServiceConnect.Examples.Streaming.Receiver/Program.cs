using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Streaming.Contracts;
using ServiceConnect.Examples.Streaming.Receiver;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "streaming-receiver";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(DocumentUploadedHandler), MessageType = typeof(DocumentUploaded) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IStreamHandler<DocumentUploaded>, DocumentUploadedHandler>();
services.AddExampleBus(settings, queueName);

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("streaming-receiver");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
