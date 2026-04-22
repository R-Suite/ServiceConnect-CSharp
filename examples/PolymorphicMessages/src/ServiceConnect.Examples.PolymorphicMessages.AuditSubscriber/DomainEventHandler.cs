using ServiceConnect.Examples.PolymorphicMessages.Contracts;
using ServiceConnect.Examples.Support.Bootstrap;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PolymorphicMessages.AuditSubscriber;

// IMessageHandler<DomainEvent>: ServiceConnect's dispatcher walks the runtime type
// hierarchy when resolving handlers, so this one handler receives OrderPlaced,
// OrderShipped, and any future DomainEvent subtype. The matching HandlerReference
// entries in Program.cs are what make the audit queue actually bound to each
// derived type's exchange — subscription setup does not walk the hierarchy.
public sealed class DomainEventHandler : IMessageHandler<DomainEvent>
{
    public IConsumeContext Context { get; set; } = null!;

    public Task HandleAsync(DomainEvent message)
    {
        var concreteTypeName = message.GetType().Name;
        var orderId = message switch
        {
            OrderPlaced placed => placed.OrderId,
            OrderShipped shipped => shipped.OrderId,
            _ => "unknown",
        };
        ConsoleStatus.Success("audit-subscriber", $"audited {concreteTypeName} {orderId}");
        return Task.CompletedTask;
    }
}
