using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Second message of the process-manager driver's three-stage saga. Correlates
/// to the persisted state established by <see cref="SagaStarted"/> via
/// <see cref="Message.CorrelationId"/>; the handler mutates the state's stage
/// counter to record the transition.
/// </summary>
public sealed class SagaIntermediate(Guid correlationId) : Message(correlationId)
{
    /// <summary>Flow-identifier discriminator echoed for the driver's payload check.</summary>
    public string Token { get; init; } = string.Empty;
}
