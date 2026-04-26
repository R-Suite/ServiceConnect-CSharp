using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.CompetingConsumers.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "competing-consumers-work";
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>([]);
services.AddExampleBus(settings, "competing-consumers-producer");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();

for (int i = 1; i <= 10; i++)
{
    var jobId = $"job-{i:D3}";
    await bus.SendAsync(
        new JobQueued(Guid.NewGuid()) { JobId = jobId },
        new SendOptions { EndPoint = queueName });
}

ConsoleStatus.Success("competing-consumers-producer", "sent 10 jobs");
await Console.Out.FlushAsync();
await Task.Delay(TimeSpan.FromSeconds(10));
