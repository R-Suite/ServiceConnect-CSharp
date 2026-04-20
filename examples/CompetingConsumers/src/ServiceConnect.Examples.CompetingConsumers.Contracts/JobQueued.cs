using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.CompetingConsumers.Contracts;

public sealed class JobQueued(Guid correlationId) : Message(correlationId)
{
    public string JobId { get; init; } = string.Empty;
}