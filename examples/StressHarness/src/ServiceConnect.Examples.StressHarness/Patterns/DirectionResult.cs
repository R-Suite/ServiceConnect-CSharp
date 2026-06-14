using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Per-direction outcome from <see cref="Orchestrator.FlowRunner.RunBothDirectionsAsync"/>.
/// One instance is produced for the α→β leg and one for the β→α leg, preserving the origin /
/// expected-receiver pair so downstream aggregators can count successes and failures per bus
/// without re-deriving the direction from a merged result. <see cref="Window"/> records the
/// chaos phase active at result-construction time so per-window roll-ups remain stable when
/// the kill / restart cadence advances mid-flow.
/// </summary>
public sealed record DirectionResult(
    Guid FlowId,
    BusIdentity Origin,
    BusIdentity ExpectedReceiver,
    bool Succeeded,
    TimeSpan Elapsed,
    int MessagesSent,
    int MessagesHandled,
    IReadOnlyList<string> AssertionFailures,
    ChaosWindow Window)
{
    public string DirectionLabel => $"{Origin.ToHeaderValue()}→{ExpectedReceiver.ToHeaderValue()}";
}
