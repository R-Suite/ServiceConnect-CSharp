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
        var perPatternResults = drivers.ToDictionary(d => d.Name, _ => new List<DirectionResult>(), StringComparer.Ordinal);

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

                var directions = await runner.RunBothDirectionsAsync(
                    driver, alpha, beta, accounting, cancellationToken).ConfigureAwait(false);

                foreach (var dir in directions)
                {
                    perPatternResults[driver.Name].Add(dir);
                    console.FlowResult(
                        string.Create(CultureInfo.InvariantCulture, $"{driver.Name} {dir.DirectionLabel}"),
                        dir.Succeeded,
                        dir.Elapsed,
                        dir.AssertionFailures.Count > 0 ? string.Join("; ", dir.AssertionFailures) : null);
                }
            }

            // Drop accounting entries for flows that have already reached their expected
            // handler count so the per-flow dictionaries stay bounded by the in-flight set.
            // The end-of-run Reconcile() still sees any under-handled flows because
            // TryRemoveCompleted only evicts rows where observed >= expected.
            accounting.TryRemoveCompleted();
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
            var dirs = kv.Value;
            var fails = dirs.SelectMany(d => d.AssertionFailures).ToList();
            var alphaPassed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Alpha && d.Succeeded);
            var alphaFailed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Alpha && !d.Succeeded);
            var betaPassed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Beta && d.Succeeded);
            var betaFailed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Beta && !d.Succeeded);
            var failedFlows = dirs
                .Where(d => !d.Succeeded)
                .Select(d => new FailedFlowDetail(
                    FlowId: d.FlowId,
                    Direction: d.DirectionLabel,
                    Failures: d.AssertionFailures))
                .ToList();
            return new PatternStats(
                Name: kv.Key,
                // Each loop iteration runs both α→β and β→α, contributing two direction
                // entries. Runs reflects the per-direction count directly so totals stay
                // comparable across smoke / soak / throughput modes.
                Runs: dirs.Count,
                Passed: alphaPassed + betaPassed,
                Failed: alphaFailed + betaFailed,
                AlphaPassed: alphaPassed,
                AlphaFailed: alphaFailed,
                BetaPassed: betaPassed,
                BetaFailed: betaFailed,
                LatencyP50Ms: Percentile(dirs, 0.50),
                LatencyP95Ms: Percentile(dirs, 0.95),
                LatencyP99Ms: Percentile(dirs, 0.99),
                AssertionFailures: fails,
                FailedFlows: failedFlows);
        }).ToList();

        return new Report(
            ReportVersion: 2,
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

    // Nearest-rank percentile over per-direction elapsed times. Returns 0 for an empty list
    // rather than throwing, so a filtered-out driver yields a zero row in the report instead
    // of being absent.
    private static double Percentile(IReadOnlyList<DirectionResult> dirs, double p)
    {
        if (dirs.Count == 0)
        {
            return 0;
        }
        var sorted = dirs.Select(d => d.Elapsed.TotalMilliseconds).OrderBy(x => x).ToArray();
        var idx = Math.Min(sorted.Length - 1, (int)(sorted.Length * p));
        return sorted[idx];
    }
}
