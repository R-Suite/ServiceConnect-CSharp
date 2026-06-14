using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.PointToPoint.Contracts;

public sealed class WorkSubmitted(Guid correlationId) : Message(correlationId)
{
    public string WorkId { get; init; } = string.Empty;
}
