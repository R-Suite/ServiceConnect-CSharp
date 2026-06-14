using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Contracts.Messages;

/// <summary>
/// First message of the process-manager driver's three-stage saga. Establishes
/// the persisted state instance under <see cref="Message.CorrelationId"/>;
/// subsequent stages reuse the same correlation id to look up and mutate the
/// stored data.
/// </summary>
public sealed class SagaStarted(Guid correlationId) : Message(correlationId)
{
    /// <summary>Flow-identifier discriminator echoed for the driver's payload check.</summary>
    public string Token { get; init; } = string.Empty;
}
