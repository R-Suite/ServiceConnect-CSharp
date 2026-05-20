using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Runs a single <see cref="IPatternDriver"/> in both directions concurrently (α→β and
/// β→α) under a per-direction wall-clock budget, returning one <see cref="DirectionResult"/>
/// per leg so downstream aggregators can count successes and failures per bus without
/// re-deriving the direction from a collapsed result. Every result is tagged with the
/// <see cref="ChaosWindow"/> read from the shared <see cref="ChaosClock"/> at the moment
/// the result is constructed, so a flow that started under <see cref="ChaosWindow.DuringChaos"/>
/// but completed under <see cref="ChaosWindow.InRecovery"/> is attributed to the recovery
/// window. The post-flow window is the meaningful one for soak roll-ups, because that's
/// the window an operator wants to read against the SLO (did the system recover, not did
/// it stay up while the killer was idle).
/// </summary>
public sealed class FlowRunner(TimeSpan flowTimeout, ChaosClock chaosClock)
{
    private readonly TimeSpan _flowTimeout = flowTimeout;
    private readonly ChaosClock _chaosClock = chaosClock;

    /// <summary>
    /// Dispatches the driver against the bus pair in both directions in parallel. Each
    /// direction gets its own linked <see cref="CancellationTokenSource"/> so a stall on
    /// one side is bounded by the flow timeout without aborting the sibling direction.
    /// The returned list is always length 2: index 0 is α→β, index 1 is β→α.
    /// </summary>
    public async Task<IReadOnlyList<DirectionResult>> RunBothDirectionsAsync(
        IPatternDriver driver,
        IBus alpha,
        IBus beta,
        FlowAccounting accounting,
        CancellationToken cancellationToken)
    {
        using var alphaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var betaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        alphaCts.CancelAfter(_flowTimeout);
        betaCts.CancelAfter(_flowTimeout);

        var alphaToBeta = RunOneAsync(driver, alpha, beta, BusIdentity.Alpha, BusIdentity.Beta, accounting, alphaCts.Token);
        var betaToAlpha = RunOneAsync(driver, beta, alpha, BusIdentity.Beta, BusIdentity.Alpha, accounting, betaCts.Token);

        var results = await Task.WhenAll(alphaToBeta, betaToAlpha).ConfigureAwait(false);
        return [results[0], results[1]];
    }

    // accounting flows through to the driver — drivers register sends through it, but the
    // runner itself only forwards the reference. Keeping the parameter named (rather than
    // discarded) preserves the call-site contract for future drivers that need it.
    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to pattern drivers; reserved by contract.")]
    private async Task<DirectionResult> RunOneAsync(
        IPatternDriver driver,
        IBus sender,
        IBus receiver,
        BusIdentity origin,
        BusIdentity expectedReceiver,
        FlowAccounting accounting,
        CancellationToken cancellationToken)
    {
        var flowId = Guid.NewGuid();
        var ctx = new StressFlowContext(
            FlowId: flowId,
            Origin: origin,
            ExpectedReceiver: expectedReceiver,
            PatternName: driver.Name,
            FlowTimeout: _flowTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await driver.RunFlowAsync(sender, receiver, ctx, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return new DirectionResult(
                FlowId: flowId,
                Origin: origin,
                ExpectedReceiver: expectedReceiver,
                Succeeded: result.Succeeded,
                Elapsed: sw.Elapsed,
                MessagesSent: result.MessagesSent,
                MessagesHandled: result.MessagesHandled,
                AssertionFailures: result.AssertionFailures,
                Window: _chaosClock.CurrentWindow);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new DirectionResult(
                FlowId: flowId,
                Origin: origin,
                ExpectedReceiver: expectedReceiver,
                Succeeded: false,
                Elapsed: sw.Elapsed,
                MessagesSent: 0,
                MessagesHandled: 0,
                AssertionFailures: [$"{driver.Name} {origin.ToHeaderValue()}→{expectedReceiver.ToHeaderValue()}: timed out after {_flowTimeout}"],
                Window: _chaosClock.CurrentWindow);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DirectionResult(
                FlowId: flowId,
                Origin: origin,
                ExpectedReceiver: expectedReceiver,
                Succeeded: false,
                Elapsed: sw.Elapsed,
                MessagesSent: 0,
                MessagesHandled: 0,
                AssertionFailures: [$"{driver.Name} {origin.ToHeaderValue()}→{expectedReceiver.ToHeaderValue()}: {ex.GetType().Name}: {ex.Message}"],
                Window: _chaosClock.CurrentWindow);
        }
    }
}
