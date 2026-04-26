using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Aggregator.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "aggregator-consumer";
var correlationId = Guid.TryParse(Environment.GetEnvironmentVariable("SC_EXAMPLES_CORRELATION_ID"), out var parsedCorrelationId)
    ? parsedCorrelationId
    : Guid.NewGuid();

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "aggregator-producer-a");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

await bus.SendAsync(
    new TelemetrySlice(correlationId)
    {
        Source = "ProducerA",
        Value = 10
    },
    new SendOptions { EndPoint = queueName });

ConsoleStatus.Success("aggregator-producer-a", "sent ProducerA/10");
