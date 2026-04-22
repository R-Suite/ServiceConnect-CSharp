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

// One handler class, three handler references. The DomainEvent entry registers
// the handler with the dispatcher for the base type; the OrderPlaced and
// OrderShipped entries bind the audit queue to those two concrete exchanges in
// RabbitMQ. Without the concrete entries the queue would only bind to the
// DomainEvent exchange — which is never published to, because DomainEvent is
// abstract — and the concrete events the publisher emits would never arrive.
var handlerReferences = new List<HandlerReference>
{
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(DomainEvent) },
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(OrderPlaced) },
    new() { HandlerType = typeof(DomainEventHandler), MessageType = typeof(OrderShipped) },
};

var services = new ServiceCollection();
services.AddSingleton<IList<HandlerReference>>(handlerReferences);
services.AddTransient<IMessageHandler<DomainEvent>, DomainEventHandler>();
services.AddExampleBus(settings, "audit-subscriber");

await using var provider = services.BuildServiceProvider();
var bus = provider.GetRequiredService<IBus>();
await bus.StartConsumingAsync();
ConsoleStatus.Ready("audit-subscriber");
await Task.Delay(Timeout.InfiniteTimeSpan);
