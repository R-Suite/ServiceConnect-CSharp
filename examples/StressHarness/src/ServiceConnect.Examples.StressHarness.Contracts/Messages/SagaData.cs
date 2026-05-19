using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Persisted state for the process-manager driver's three-stage saga.
/// <see cref="Stage"/> ratchets from 0 → 1 → 2 → 3 as each message arrives,
/// so the driver can assert the data was correlated, mutated, and progressed
/// to the final stage across distinct messages sharing one correlation id.
/// </summary>
/// <remarks>
/// The class is mutable (settable properties + parameterless constructor) because
/// the framework's <c>IProcessManagerData</c> contract requires <c>new()</c> and
/// the dispatcher mutates instances in place between persistence reads and writes.
/// </remarks>
public sealed class SagaData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }

    /// <summary>Monotonically-advancing stage counter — 1 for Started, 2 for Intermediate, 3 for Completed.</summary>
    public int Stage { get; set; }
}
