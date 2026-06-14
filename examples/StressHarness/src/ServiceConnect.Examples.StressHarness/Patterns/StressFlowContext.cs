namespace ServiceConnect.Examples.StressHarness.Patterns;

public sealed record StressFlowContext(
    Guid FlowId,
    BusIdentity Origin,
    BusIdentity ExpectedReceiver,
    string PatternName,
    TimeSpan FlowTimeout);
