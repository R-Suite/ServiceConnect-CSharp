using System.Diagnostics;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Cli;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Reporting;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Rate-controlled run strategy. Issues one round of selected driver flows per tick, where
/// the tick period derives from <see cref="HarnessCliOptions.Rate"/> (flows per second).
/// Captures per-driver round-trip latency on every flow and reports p50 / p95 / p99 in the
/// emitted <see cref="Report"/>. Memory baseline / final snapshots mirror the soak-mode
/// check so a rate-driven leak still trips <see cref="MemoryAssertions.CheckDelta"/>.
/// </summary>
/// <remarks>
/// The loop targets a soft cadence — if a tick overruns the budget the next tick fires
/// immediately rather than coalescing the slip, so latency reports remain meaningful under
/// broker back-pressure without distorting subsequent ticks' wall-clock spacing.
/// </remarks>
public static class ThroughputLoop
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
        _ = console;     // ConsoleReporter is reserved for future progress lines; the rate
                         // loop intentionally stays silent per flow to avoid distorting the
                         // measured latency with synchronous console I/O.

        var startedAt = DateTimeOffset.UtcNow;
        var baseline = MemoryAssertions.SnapshotTotalMemory();
        var runner = new FlowRunner(opts.FlowTimeout);
        var perPatternResults = drivers.ToDictionary(d => d.Name, _ => new List<DirectionResult>(), StringComparer.Ordinal);

        // Rate is flows per second per pattern slot, so the inter-tick period is the
        // reciprocal in milliseconds. Each tick visits every selected driver once, which
        // means the effective per-driver rate is the configured Rate value rather than
        // Rate / driverCount — operators tune Rate as the target tick frequency.
        var interval = TimeSpan.FromMilliseconds(1000.0 / opts.Rate);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < opts.Duration && !cancellationToken.IsCancellationRequested)
        {
            var tickStart = sw.Elapsed;
            foreach (var driver in drivers)
            {
                if (opts.Patterns is not null && !opts.Patterns.Contains(driver.Name))
                {
                    continue;
                }

                var directions = await runner.RunBothDirectionsAsync(
                    driver, alpha, beta, accounting, cancellationToken).ConfigureAwait(false);

                foreach (var dir in directions)
                {
                    perPatternResults[driver.Name].Add(dir);
                }
            }

            // Drop accounting entries for flows that have already reached their expected
            // handler count so the per-flow dictionaries stay bounded by the in-flight set.
            // The end-of-run Reconcile() still sees any under-handled flows because
            // TryRemoveCompleted only evicts rows where observed >= expected.
            accounting.TryRemoveCompleted();

            var tickElapsed = sw.Elapsed - tickStart;
            var sleep = interval - tickElapsed;
            if (sleep > TimeSpan.Zero)
            {
                await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
            }
        }

        var final = MemoryAssertions.SnapshotTotalMemory();
        var processFailures = new List<string>();
        var memCheck = MemoryAssertions.CheckDelta(baseline, final, opts.MemoryBudgetBytes);
        if (!memCheck.Ok)
        {
            processFailures.Add(memCheck.Failure);
        }

        var stats = perPatternResults.Select(kv =>
        {
            var dirs = kv.Value;
            var fails = dirs.SelectMany(d => d.AssertionFailures).ToList();
            var alphaPassed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Alpha && d.Succeeded);
            var alphaFailed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Alpha && !d.Succeeded);
            var betaPassed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Beta && d.Succeeded);
            var betaFailed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Beta && !d.Succeeded);
            return new PatternStats(
                Name: kv.Key,
                // Each tick contributes one α→β plus one β→α direction; Runs is the total
                // direction count, matching smoke and soak modes.
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
                AssertionFailures: fails);
        }).ToList();

        var completedAt = DateTimeOffset.UtcNow;
        return new Report(
            ReportVersion: 2,
            Mode: "throughput",
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
