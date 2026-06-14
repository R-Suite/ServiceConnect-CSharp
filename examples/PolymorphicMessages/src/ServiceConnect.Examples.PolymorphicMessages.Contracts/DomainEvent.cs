using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PolymorphicMessages.Contracts;

public abstract class DomainEvent(Guid correlationId) : Message(correlationId)
{
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
