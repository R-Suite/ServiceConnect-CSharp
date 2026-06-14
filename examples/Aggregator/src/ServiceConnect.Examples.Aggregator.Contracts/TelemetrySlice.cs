using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.Aggregator.Contracts;

public sealed class TelemetrySlice(Guid correlationId) : Message(correlationId)
{
    public string Source { get; init; } = string.Empty;

    public int Value { get; init; }
}
