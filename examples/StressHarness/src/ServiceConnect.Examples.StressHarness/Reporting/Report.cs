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

public sealed record PatternStats(
    string Name,
    int Runs,
    int Passed,
    int Failed,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyP99Ms,
    IReadOnlyList<string> AssertionFailures);
