using System.Diagnostics;
using System.Globalization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
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
/// When <paramref name="chaosScheduler"/> is supplied, the scheduler's kill / restart loop
/// runs concurrently with the soak iterations; at end-of-soak the scheduler is cancelled,
/// awaited, the clock advances through <see cref="ChaosWindow.InRecovery"/> for the
/// recovery budget then <see cref="ChaosWindow.PostChaos"/>, and the bus pair is checked
/// for consume liveness via <see cref="RecoveryAssertion"/>.
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
        ReportMetadata metadata,
        IReadOnlyList<IFlowKeyedSingleton> flowKeyedSingletons,
        ChaosClock chaosClock,
        ChaosScheduler? chaosScheduler,
        TimeSpan chaosRecoveryBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(alpha);
        ArgumentNullException.ThrowIfNull(beta);
        ArgumentNullException.ThrowIfNull(accounting);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(flowKeyedSingletons);
        ArgumentNullException.ThrowIfNull(chaosClock);

        var startedAt = DateTimeOffset.UtcNow;
        var baseline = MemoryAssertions.SnapshotTotalMemory();
        var runner = new FlowRunner(opts.FlowTimeout, chaosClock);
        var perPatternResults = drivers.ToDictionary(d => d.Name, _ => new List<DirectionResult>(), StringComparer.Ordinal);
        var processFailures = new List<string>();

        // The scheduler runs on its own task linked to the soak's cancellation token so an
        // operator Ctrl-C cancels both loops at the same instant. The try/finally below
        // ensures that even if the soak body raises, the scheduler is cancelled and
        // awaited before the recovery wait begins — the recovery measurement is only
        // meaningful once the killer has stopped issuing new kills.
        Task? schedulerTask = null;
        CancellationTokenSource? schedulerCts = null;
        if (chaosScheduler is not null)
        {
            schedulerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            schedulerTask = Task.Run(() => chaosScheduler.RunAsync(schedulerCts.Token), cancellationToken);
        }

        // Switch the console into soak mode for the duration of the loop. The reporter
        // suppresses per-flow PASS lines (replacing them with a periodic heartbeat) and
        // still prints failures inline. The finally ensures the heartbeat timer is
        // released even if a driver raises mid-loop, so the disposer in Program.cs has
        // nothing left to clean up.
        console.BeginSoakMode(TimeSpan.FromSeconds(5));
        try
        {
            try
            {
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

                        // Reclaim per-flow accumulator rows for the flows that succeeded on
                        // this driver this tick. Failed flows are deliberately retained so
                        // their per-flow detail survives into report.md. Each accumulator's
                        // TryRemoveCompleted is idempotent and tolerates ids it never saw,
                        // so the loop sweeps every singleton without per-driver routing.
                        var completedIds = directions.Where(d => d.Succeeded).Select(d => d.FlowId).ToList();
                        if (completedIds.Count > 0)
                        {
                            foreach (var singleton in flowKeyedSingletons)
                            {
                                singleton.TryRemoveCompleted(completedIds);
                            }
                        }
                    }

                    // Drop accounting entries for flows that have already reached their expected
                    // handler count so the per-flow dictionaries stay bounded by the in-flight set.
                    // The end-of-run Reconcile() still sees any under-handled flows because
                    // TryRemoveCompleted only evicts rows where observed >= expected.
                    accounting.TryRemoveCompleted();
                }
            }
            finally
            {
                // Cancel + await the scheduler before the recovery wait so no further kills
                // race the recovery measurement. The scheduler swallows its own
                // OperationCanceledException internally, so awaiting here propagates only
                // genuine faults (e.g. a chaos surface that threw on RestartNodeAsync).
                if (chaosScheduler is not null && schedulerCts is not null)
                {
                    await schedulerCts.CancelAsync().ConfigureAwait(false);
                    if (schedulerTask is not null)
                    {
                        await schedulerTask.ConfigureAwait(false);
                    }
                    schedulerCts.Dispose();
                }
            }
        }
        finally
        {
            console.EndSoakMode();
        }

        // Recovery phase only runs when chaos was active. The clock advances into
        // InRecovery so any flow still in flight (drivers run synchronously inside
        // the loop body, so this is a no-op for the current shape but documents the
        // intent for future async drivers) is tagged appropriately; after the wait
        // expires the clock moves to PostChaos for any subsequent assertion. The
        // recovery check uses a fixed 30 s budget to confirm consume liveness on
        // both buses; the configured recovery budget governs how long the harness
        // gives the broker to settle before the check fires.
        if (chaosScheduler is not null)
        {
            chaosClock.SetWindow(ChaosWindow.InRecovery);
            try
            {
                await Task.Delay(chaosRecoveryBudget, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // user-cancelled mid-recovery; fall through to the consume check anyway so
                // the report still reflects the bus state at shutdown.
            }
            chaosClock.SetWindow(ChaosWindow.PostChaos);

            var recoveryCheck = await RecoveryAssertion.CheckBothBusesConsumingAsync(
                alpha, beta, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!recoveryCheck.Ok)
            {
                processFailures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"chaos: {recoveryCheck.Failure}"));
            }
        }

        var final = MemoryAssertions.SnapshotTotalMemory();
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

        // "Extra handler firings beyond what was sent" — the per-flow excess summed
        // across every flow whose observed handler count exceeded the expected. Directly
        // comparable to the under-handled count: under-handled measures lost messages,
        // this measures broker-redelivered ones. Under chaos both are expected; outside
        // a chaos window a non-zero value here is an exactly-once finding.
        var duplicateInvocations = acct.DuplicatedFlows.Sum(d => d.Observed - d.Expected);

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

        // Chaos roll-up is populated only when the scheduler ran. A non-chaos soak
        // leaves Chaos null so consumers can tell "chaos disabled" from "chaos
        // enabled but produced zero kills" (the latter would be a misconfiguration
        // — interval longer than the soak duration — and is worth surfacing).
        // Each DirectionResult was stamped with the active window by FlowRunner;
        // this pass partitions the per-pattern lists by that stamp.
        ChaosWindowStats? chaosStats = null;
        if (chaosScheduler is not null)
        {
            var perPatternBreakdown = perPatternResults.Select(kv =>
            {
                var dirs = kv.Value;
                return new ChaosPatternBreakdown(
                    PatternName: kv.Key,
                    PreChaosCount: dirs.Count(d => d.Window == ChaosWindow.PreChaos),
                    DuringChaosCount: dirs.Count(d => d.Window == ChaosWindow.DuringChaos),
                    InRecoveryCount: dirs.Count(d => d.Window == ChaosWindow.InRecovery),
                    PostChaosCount: dirs.Count(d => d.Window == ChaosWindow.PostChaos));
            }).ToList();

            var events = chaosScheduler.Events
                .Select(e => new ChaosEventSummary(e.KilledAt, e.RestartedAt, e.NodeName))
                .ToList();

            chaosStats = new ChaosWindowStats(
                KillEventCount: events.Count,
                Events: events,
                PerPattern: perPatternBreakdown,
                DuplicateHandlerInvocations: duplicateInvocations);
        }

        return new Report(
            ReportVersion: 3,
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
            ProcessAssertionFailures: processFailures,
            Metadata: metadata,
            Chaos: chaosStats);
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
