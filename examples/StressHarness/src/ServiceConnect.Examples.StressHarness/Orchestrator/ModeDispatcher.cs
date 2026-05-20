using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Cli;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Reporting;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Selects a run strategy (<c>smoke</c> / <c>soak</c> / <c>throughput</c>) based on the
/// CLI mode and drives the registered <see cref="IPatternDriver"/>s against the live
/// bus pair owned by <see cref="HarnessHost"/>. Aggregates per-pattern outcomes and the
/// process-level <see cref="FlowAccounting"/> reconciliation into a single
/// <see cref="Report"/>.
/// </summary>
public sealed class ModeDispatcher(
    HarnessCliOptions opts,
    IReadOnlyList<IPatternDriver> drivers,
    HarnessHost host,
    FlowAccounting accounting,
    ConsoleReporter console,
    ILogger<ModeDispatcher> logger)
{
    private readonly HarnessCliOptions _opts = opts;
    private readonly IReadOnlyList<IPatternDriver> _drivers = drivers;
    private readonly HarnessHost _host = host;
    private readonly FlowAccounting _accounting = accounting;
    private readonly ConsoleReporter _console = console;

    // Logger reserved for soak/throughput modes which need progress logging beyond the
    // per-flow console reporter. Smoke mode prints directly through the reporter so this
    // field is currently dormant.
    [SuppressMessage("CodeQuality", "IDE0052", Justification = "Reserved for soak/throughput modes.")]
    private readonly ILogger<ModeDispatcher> _logger = logger;

    public Task<Report> RunAsync(CancellationToken cancellationToken) => _opts.Mode switch
    {
        "smoke" => RunSmokeAsync(cancellationToken),
        "soak" => SoakLoop.RunAsync(_opts, _drivers, _host.Alpha, _host.Beta, _accounting, _console, cancellationToken),
        "throughput" => ThroughputLoop.RunAsync(_opts, _drivers, _host.Alpha, _host.Beta, _accounting, _console, cancellationToken),
        _ => throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"unknown mode {_opts.Mode}")),
    };

    private async Task<Report> RunSmokeAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var baseline = MemoryAssertions.SnapshotTotalMemory();
        var runner = new FlowRunner(_opts.FlowTimeout);
        var perPattern = new Dictionary<string, List<DirectionResult>>(StringComparer.Ordinal);
        var processFailures = new List<string>();

        foreach (var driver in _drivers)
        {
            if (_opts.Patterns is not null && !_opts.Patterns.Contains(driver.Name))
            {
                continue;
            }

            _console.Heartbeat(tick: 1, totalTicks: 1, driver.Name);

            var directions = await runner.RunBothDirectionsAsync(
                driver, _host.Alpha, _host.Beta, _accounting, cancellationToken).ConfigureAwait(false);

            if (!perPattern.TryGetValue(driver.Name, out var bucket))
            {
                bucket = [];
                perPattern[driver.Name] = bucket;
            }

            foreach (var dir in directions)
            {
                bucket.Add(dir);
                _console.FlowResult(
                    string.Create(CultureInfo.InvariantCulture, $"{driver.Name} {dir.DirectionLabel}"),
                    dir.Succeeded,
                    dir.Elapsed,
                    dir.AssertionFailures.Count > 0 ? string.Join("; ", dir.AssertionFailures) : null);
            }
        }

        var acct = _accounting.Reconcile();
        if (acct.MissingFlows.Count > 0)
        {
            processFailures.Add(string.Create(CultureInfo.InvariantCulture,
                $"flow accounting: {acct.MissingFlows.Count} flow(s) sent but under-handled"));
        }
        if (acct.UnexpectedFlows.Count > 0)
        {
            processFailures.Add(string.Create(CultureInfo.InvariantCulture,
                $"flow accounting: {acct.UnexpectedFlows.Count} flow(s) handled without record of send"));
        }

        // Lifecycle assertion runs last because it disposes the alpha bus — no further
        // driver work can be issued through it afterwards. The host's own DisposeAsync
        // (fired by Program.cs's `await using`) tolerates a pre-disposed bus, so a second
        // dispose on the same instance is a no-op rather than a fault.
        var lifecycleCheck = await LifecycleAssertions.DisposeDuringFlowAsync(
            _host.Alpha, _host.Beta, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!lifecycleCheck.Ok)
        {
            processFailures.Add(string.Create(CultureInfo.InvariantCulture,
                $"lifecycle: {lifecycleCheck.Failure}"));
        }

        var final = MemoryAssertions.SnapshotTotalMemory();
        var completedAt = DateTimeOffset.UtcNow;

        var patternStats = perPattern.Select(kv =>
        {
            var dirs = kv.Value;
            var fails = dirs.SelectMany(d => d.AssertionFailures).ToList();
            var alphaPassed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Alpha && d.Succeeded);
            var alphaFailed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Alpha && !d.Succeeded);
            var betaPassed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Beta && d.Succeeded);
            var betaFailed = dirs.Count(d => d.ExpectedReceiver == BusIdentity.Beta && !d.Succeeded);
            return new PatternStats(
                Name: kv.Key,
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

        var totalFlows = patternStats.Sum(p => p.Runs);
        var passedFlows = patternStats.Sum(p => p.Passed);
        var failedFlows = patternStats.Sum(p => p.Failed);

        return new Report(
            ReportVersion: 2,
            Mode: _opts.Mode,
            StartedAtUtc: startedAt,
            CompletedAtUtc: completedAt,
            Duration: completedAt - startedAt,
            MemoryBaselineBytes: baseline,
            MemoryFinalBytes: final,
            TotalFlows: totalFlows,
            PassedFlows: passedFlows,
            FailedFlows: failedFlows,
            Patterns: patternStats,
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
