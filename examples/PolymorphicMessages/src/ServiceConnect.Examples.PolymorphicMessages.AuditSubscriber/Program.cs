using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Examples.PolymorphicMessages.AuditSubscriber;
using ServiceConnect.Examples.PolymorphicMessages.Contracts;
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

// One handler class, registered against each CONCRETE event it audits. Each entry binds the
// audit queue to that concrete exchange; the dispatcher's type-hierarchy walk routes the
// delivery to DomainEventHandler (registered for the base type below). The base type is NOT
// listed: the publisher fans every derived publish out to its own exchange AND every ancestor
// exchange, so also binding the DomainEvent exchange would deliver each event twice — and that
// base copy can't be deserialised, since DomainEvent is abstract.
var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(OrderPlaced) },
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(OrderShipped) },
};

var services = new ServiceCollection();
services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<DomainEvent>, DomainEventHandler>();
services.AddExampleBus(settings, "audit-subscriber");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("audit-subscriber");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.InfiniteTimeSpan);
