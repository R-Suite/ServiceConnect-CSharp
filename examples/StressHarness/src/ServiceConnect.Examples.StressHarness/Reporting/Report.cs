namespace ServiceConnect.Examples.StressHarness.Reporting;

public sealed record ReportMetadata(
    string Hostname,
    string RuntimeVersion,
    string BrokerUri,
    string PersistenceMode);

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
    IReadOnlyList<string> ProcessAssertionFailures,
    ReportMetadata Metadata,
    ChaosWindowStats? Chaos);

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

/// <summary>
/// Single kill / restart cycle observed by <see cref="Chaos.ChaosScheduler"/>.
/// Mirrors <see cref="Chaos.ChaosEvent"/> in a report-friendly shape so the
/// JSON / markdown writers don't drag the scheduler type into their surface.
/// </summary>
public sealed record ChaosEventSummary(
    DateTimeOffset KilledAt,
    DateTimeOffset RestartedAt,
    string NodeName);

/// <summary>
/// Per-pattern direction counts split by the <see cref="Chaos.ChaosWindow"/>
/// stamped on each completed <see cref="Patterns.DirectionResult"/>. Soft
/// signal — never fails the run; populated only when the soak's chaos
/// scheduler was active.
/// </summary>
public sealed record ChaosPatternBreakdown(
    string PatternName,
    int PreChaosCount,
    int DuringChaosCount,
    int InRecoveryCount,
    int PostChaosCount);

/// <summary>
/// Top-level chaos roll-up attached to <see cref="Report.Chaos"/> when the
/// soak ran with <c>--chaos docker</c>. Smoke / throughput / non-chaos soak
/// runs report a null value so consumers can distinguish "chaos not run"
/// from "chaos run but zero kills" (the latter is a configuration smell —
/// the scheduler interval was longer than the soak duration).
/// </summary>
public sealed record ChaosWindowStats(
    int KillEventCount,
    IReadOnlyList<ChaosEventSummary> Events,
    IReadOnlyList<ChaosPatternBreakdown> PerPattern);
