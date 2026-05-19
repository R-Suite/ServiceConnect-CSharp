using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Work unit sent through the competing-consumers driver's batch send. The driver
/// publishes N items to the receiver's queue and asserts that both registered
/// <c>IMessageHandler&lt;WorkItem&gt;</c> instances saw at least one delivery.
/// </summary>
/// <remarks>
/// <para>
/// The <c>CorrelationId</c> base-class property carries the flow identifier so every
/// item in the batch correlates back to the same accounting record. <see cref="Sequence"/>
/// disambiguates individual items within the batch — useful when inspecting broker
/// captures during a failure investigation, even though the assertion itself only
/// inspects the per-handler counter table.
/// </para>
/// </remarks>
public sealed class WorkItem(Guid correlationId) : Message(correlationId)
{
    /// <summary>
    /// Per-batch sequence number assigned by the driver (1..N). Aids broker-capture
    /// correlation when reproducing a partial-fanout failure.
    /// </summary>
    public int Sequence { get; init; }
}
