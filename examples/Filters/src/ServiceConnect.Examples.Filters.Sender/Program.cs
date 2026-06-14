using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.Filters.Contracts;
using ServiceConnect.Examples.Filters.Sender;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

var settings = ExampleSettingsLoader.Load();
var queueName = Environment.GetEnvironmentVariable("SC_EXAMPLES_QUEUE_NAME") ?? "filters-consumer";

await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>([]);
services.AddSingleton<TraceHeaderFilter>();
services.AddExampleBus(settings, "filters-sender", configureBuilder: builder =>
{
    builder.AddOutgoingFilter<TraceHeaderFilter>();
});

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.SendAsync(
    new FilteredNotification(Guid.NewGuid()) { MessageText = "filter applied" },
    new SendOptions { EndPoint = queueName });
ConsoleStatus.Success("filters-sender", "sent filter applied");
await Console.Out.FlushAsync();
