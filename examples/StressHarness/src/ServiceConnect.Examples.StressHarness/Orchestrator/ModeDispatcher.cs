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
        var perPattern = new Dictionary<string, List<FlowResult>>(StringComparer.Ordinal);
        var processFailures = new List<string>();

        foreach (var driver in _drivers)
        {
            if (_opts.Patterns is not null && !_opts.Patterns.Contains(driver.Name))
            {
                continue;
            }

            _console.Heartbeat(tick: 1, totalTicks: 1, driver.Name);

            var result = await runner.RunBothDirectionsAsync(
                driver, _host.Alpha, _host.Beta, _accounting, cancellationToken).ConfigureAwait(false);
            perPattern[driver.Name] = [result];

            _console.FlowResult(
                driver.Name,
                result.Succeeded,
                result.Elapsed,
                result.AssertionFailures.Count > 0 ? string.Join("; ", result.AssertionFailures) : null);
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

        var final = MemoryAssertions.SnapshotTotalMemory();
        var completedAt = DateTimeOffset.UtcNow;

        var patternStats = perPattern.Select(kv =>
        {
            var fails = kv.Value.SelectMany(r => r.AssertionFailures).ToList();
            var passedRuns = kv.Value.Count(r => r.Succeeded);
            return new PatternStats(
                Name: kv.Key,
                Runs: kv.Value.Count * 2,
                Passed: (passedRuns * 2) - fails.Count,
                Failed: fails.Count,
                LatencyP50Ms: 0,
                LatencyP95Ms: 0,
                LatencyP99Ms: 0,
                AssertionFailures: fails);
        }).ToList();

        var totalFlows = patternStats.Sum(p => p.Runs);
        var passedFlows = patternStats.Sum(p => p.Passed);
        var failedFlows = totalFlows - passedFlows;

        return new Report(
            ReportVersion: 1,
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
}
