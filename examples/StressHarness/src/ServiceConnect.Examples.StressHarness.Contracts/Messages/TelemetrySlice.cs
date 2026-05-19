using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Single item batched by the aggregator driver. Inherits <see cref="Message"/>
/// so the framework's aggregator routing applies (<see cref="Aggregator{T}"/>
/// constraint <c>T : Message</c>).
/// </summary>
public sealed class TelemetrySlice(Guid correlationId) : Message(correlationId)
{
    /// <summary>Integer payload — summed by the aggregator for the driver's batch-size assertion.</summary>
    public int Value { get; init; }
}
