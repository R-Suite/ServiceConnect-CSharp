using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// Final message of the process-manager driver's three-stage saga. Correlates
/// to the persisted state under <see cref="Message.CorrelationId"/>; the
/// handler marks the saga's stage counter as terminal so the driver can verify
/// the full progression Started → Intermediate → Completed.
/// </summary>
public sealed class SagaCompleted(Guid correlationId) : Message(correlationId)
{
    /// <summary>Flow-identifier discriminator echoed for the driver's payload check.</summary>
    public string Token { get; init; } = string.Empty;
}
