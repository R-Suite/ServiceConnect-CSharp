namespace ServiceConnect.Examples.StressHarness.Reporting;

public sealed record Report(
    int ReportVersion,
    string Mode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration,
    long MemoryBaselineBytes,
    long MemoryFinalBytes,
    int TotalFlows,
    int PassedFlows,
    int FailedFlows,
    IReadOnlyList<PatternStats> Patterns,
    IReadOnlyList<string> ProcessAssertionFailures);

public sealed record FailedFlowDetail(
    Guid FlowId,
    string Direction,
    IReadOnlyList<string> Failures);

public sealed record PatternStats(
    string Name,
    int Runs,
    int Passed,
    int Failed,
    int AlphaPassed,
    int AlphaFailed,
    int BetaPassed,
    int BetaFailed,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyP99Ms,
    IReadOnlyList<string> AssertionFailures,
    IReadOnlyList<FailedFlowDetail> FailedFlows);
