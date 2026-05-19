using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Runs a single <see cref="IPatternDriver"/> in both directions concurrently (α→β and
/// β→α) under a per-direction wall-clock budget, and merges the two outcomes into a
/// single <see cref="FlowResult"/>. The merged elapsed is the slower of the two so the
/// caller's per-flow timing reflects the bottleneck direction.
/// </summary>
public sealed class FlowRunner(TimeSpan flowTimeout)
{
    private readonly TimeSpan _flowTimeout = flowTimeout;

    /// <summary>
    /// Dispatches the driver against the bus pair in both directions in parallel. Each
    /// direction gets its own linked <see cref="CancellationTokenSource"/> so a stall on
    /// one side is bounded by the flow timeout without aborting the sibling direction.
    /// Returns a merged result regardless of which side failed.
    /// </summary>
    public async Task<FlowResult> RunBothDirectionsAsync(
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
        return MergeResults(results[0], results[1]);
    }

    // accounting flows through to the driver — drivers register sends through it, but the
    // runner itself only forwards the reference. Keeping the parameter named (rather than
    // discarded) preserves the call-site contract for future drivers that need it.
    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to pattern drivers; reserved by contract.")]
    private async Task<FlowResult> RunOneAsync(
        IPatternDriver driver,
        IBus sender,
        IBus receiver,
        BusIdentity origin,
        BusIdentity expectedReceiver,
        FlowAccounting accounting,
        CancellationToken cancellationToken)
    {
        var ctx = new StressFlowContext(
            FlowId: Guid.NewGuid(),
            Origin: origin,
            ExpectedReceiver: expectedReceiver,
            PatternName: driver.Name,
            FlowTimeout: _flowTimeout);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await driver.RunFlowAsync(sender, receiver, ctx, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return result;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return FlowResult.Fail(sw.Elapsed, sent: 0, handled: 0,
                $"{driver.Name} {origin}→{expectedReceiver}: timed out after {_flowTimeout}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FlowResult.Fail(sw.Elapsed, sent: 0, handled: 0,
                $"{driver.Name} {origin}→{expectedReceiver}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static FlowResult MergeResults(FlowResult a, FlowResult b) => new(
        Succeeded: a.Succeeded && b.Succeeded,
        Elapsed: a.Elapsed > b.Elapsed ? a.Elapsed : b.Elapsed,
        MessagesSent: a.MessagesSent + b.MessagesSent,
        MessagesHandled: a.MessagesHandled + b.MessagesHandled,
        AssertionFailures: [.. a.AssertionFailures, .. b.AssertionFailures]);
}
