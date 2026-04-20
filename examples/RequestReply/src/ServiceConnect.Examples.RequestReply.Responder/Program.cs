using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.RequestReply.Contracts;
using ServiceConnect.Examples.RequestReply.Responder;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Examples.Support.Configuration;
using ServiceConnect.Interfaces;

var settings = ExampleSettingsLoader.Load();
await DependencyWaiter.WaitForRabbitMqAsync(
    settings.RabbitMqHost,
    settings.RabbitMqPort,
    settings.RabbitMqUsername,
    settings.RabbitMqPassword,
    CancellationToken.None);

var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(QuoteRequestHandler), MessageType = typeof(QuoteRequest) }
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<QuoteRequest>, QuoteRequestHandler>();
services.AddExampleBus(settings, "request-reply-responder");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("request-reply-responder");
await Task.Delay(Timeout.InfiniteTimeSpan);