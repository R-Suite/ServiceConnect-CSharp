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
        var latencies = drivers.ToDictionary(d => d.Name, _ => new List<long>(), StringComparer.Ordinal);
        var fails = drivers.ToDictionary(d => d.Name, _ => new List<string>(), StringComparer.Ordinal);

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

                var result = await runner.RunBothDirectionsAsync(
                    driver, alpha, beta, accounting, cancellationToken).ConfigureAwait(false);
                latencies[driver.Name].Add((long)result.Elapsed.TotalMilliseconds);
                if (!result.Succeeded)
                {
                    fails[driver.Name].AddRange(result.AssertionFailures);
                }
            }

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

        var stats = drivers.Select(d =>
        {
            var lat = latencies[d.Name];
            lat.Sort();
            // Nearest-rank percentile. The sorted list is already in ascending order, so
            // floor(count * p) gives the zero-based index of the requested percentile; the
            // Math.Min clamp protects the p99 case when count * 0.99 rounds up to count.
            // Returns 0 for an empty list rather than throwing — a driver filtered out by
            // --patterns contributes a zero-row to the report instead of being absent.
            double Pct(double p) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)(lat.Count * p))];
            var passed = (lat.Count * 2) - fails[d.Name].Count;
            return new PatternStats(
                Name: d.Name,
                // Each entry in latencies[] represents one RunBothDirectionsAsync call,
                // which executed both α→β and β→α, so total run count is double the
                // sample count. Matches the soak / smoke convention.
                Runs: lat.Count * 2,
                Passed: passed,
                Failed: fails[d.Name].Count,
                LatencyP50Ms: Pct(0.50),
                LatencyP95Ms: Pct(0.95),
                LatencyP99Ms: Pct(0.99),
                AssertionFailures: fails[d.Name]);
        }).ToList();

        var completedAt = DateTimeOffset.UtcNow;
        return new Report(
            ReportVersion: 1,
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
}
