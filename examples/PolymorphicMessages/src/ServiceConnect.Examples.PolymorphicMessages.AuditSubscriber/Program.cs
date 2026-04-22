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

// One handler class, three handler references. Registering HandlerReference for
// each derived type is what binds the audit queue to each derived type's
// exchange in RabbitMQ. Without the OrderPlaced and OrderShipped entries below,
// the queue would only be bound to the DomainEvent exchange and would never
// receive the concrete events that the publisher emits.
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
