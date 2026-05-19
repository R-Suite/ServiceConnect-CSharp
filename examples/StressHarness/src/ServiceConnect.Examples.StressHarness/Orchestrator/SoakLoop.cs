using System.Diagnostics;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Cli;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Reporting;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Continuous-tick run strategy. Iterates every selected <see cref="IPatternDriver"/> in
/// both directions repeatedly until <see cref="HarnessCliOptions.Duration"/> elapses or the
/// supplied <see cref="CancellationToken"/> trips. Wraps the loop with start / finish
/// memory snapshots so the produced <see cref="Report"/> exposes the resident-memory delta
/// for <see cref="MemoryAssertions.CheckDelta"/> to evaluate against the CLI memory budget.
/// </summary>
/// <remarks>
/// Per-pattern latency percentiles are not computed in soak mode — the goal is a long-running
/// allocation / leak check, not a throughput profile. <see cref="ThroughputLoop"/> covers
/// latency reporting under a rate-controlled schedule.
/// </remarks>
public static class SoakLoop
{
    public static async Task<Report> RunAsync(
        HarnessCliOptions opts,
        IReadOnlyList<IPatternDriver> drivers,
        IBus alpha,
        IBus beta,
        FlowAccounting accounting,
        ConsoleReporter console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(alpha);
        ArgumentNullException.ThrowIfNull(beta);
        ArgumentNullException.ThrowIfNull(accounting);
        ArgumentNullException.ThrowIfNull(console);

        var startedAt = DateTimeOffset.UtcNow;
        var baseline = MemoryAssertions.SnapshotTotalMemory();
        var runner = new FlowRunner(opts.FlowTimeout);
        var perPatternResults = drivers.ToDictionary(d => d.Name, _ => new List<FlowResult>(), StringComparer.Ordinal);

        var sw = Stopwatch.StartNew();
        var tick = 0;
        while (sw.Elapsed < opts.Duration && !cancellationToken.IsCancellationRequested)
        {
            tick++;
            foreach (var driver in drivers)
            {
                if (opts.Patterns is not null && !opts.Patterns.Contains(driver.Name))
                {
                    continue;
                }

                // Soak mode has no fixed tick total — the loop runs until the wall-clock
                // budget expires. Pass -1 so the console line reads "[tick N/-1]"; the
                // reporter does no arithmetic on the value and downstream consumers treat
                // the negative as "unknown total".
                console.Heartbeat(tick, totalTicks: -1, driver.Name);

                var result = await runner.RunBothDirectionsAsync(
                    driver, alpha, beta, accounting, cancellationToken).ConfigureAwait(false);
                perPatternResults[driver.Name].Add(result);

                console.FlowResult(
                    driver.Name,
                    result.Succeeded,
                    result.Elapsed,
                    result.AssertionFailures.Count > 0 ? string.Join("; ", result.AssertionFailures) : null);
            }
        }

        var final = MemoryAssertions.SnapshotTotalMemory();
        var processFailures = new List<string>();
        var memCheck = MemoryAssertions.CheckDelta(baseline, final, opts.MemoryBudgetBytes);
        if (!memCheck.Ok)
        {
            processFailures.Add(memCheck.Failure);
        }

        var acct = accounting.Reconcile();
        if (acct.MissingFlows.Count > 0)
        {
            processFailures.Add(string.Create(CultureInfo.InvariantCulture,
                $"flow accounting: {acct.MissingFlows.Count} flow(s) sent but under-handled at end of soak"));
        }

        var completedAt = DateTimeOffset.UtcNow;
        var stats = perPatternResults.Select(kv =>
        {
            var fails = kv.Value.SelectMany(r => r.AssertionFailures).ToList();
            var passedRuns = kv.Value.Count(r => r.Succeeded);
            return new PatternStats(
                Name: kv.Key,
                // Each RunBothDirectionsAsync invocation runs the driver α→β AND β→α, so
                // two flows execute per loop iteration. Multiplying the iteration count
                // by two keeps the totals comparable across smoke / soak / throughput.
                Runs: kv.Value.Count * 2,
                Passed: (passedRuns * 2) - fails.Count,
                Failed: fails.Count,
                LatencyP50Ms: 0,
                LatencyP95Ms: 0,
                LatencyP99Ms: 0,
                AssertionFailures: fails);
        }).ToList();

        return new Report(
            ReportVersion: 1,
            Mode: "soak",
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            Duration: completedAt - startedAt,
            MemoryBaselineBytes: baseline,
            MemoryFinalBytes: final,
            TotalFlows: stats.Sum(s => s.Runs),
            PassedFlows: stats.Sum(s => s.Passed),
            FailedFlows: stats.Sum(s => s.Failed),
            Patterns: stats,
            ProcessAssertionFailures: processFailures);
    }
}
