namespace ServiceConnect.Examples.StressHarness.Chaos;

/// <summary>
/// Audit record produced by <see cref="ChaosScheduler"/> for each
/// kill/restart cycle: the wall-clock timestamps at which the node was
/// stopped and started again, plus the node identifier passed to the
/// chaos surface.
/// </summary>
public sealed record ChaosEvent(DateTimeOffset KilledAt, DateTimeOffset RestartedAt, string NodeName);

/// <summary>
/// Background loop that exercises an <see cref="IBrokerChaos"/> on a
/// fixed cadence: wait for <c>interval</c>, kill the node, advance the
/// shared <see cref="ChaosClock"/> to <see cref="ChaosWindow.DuringChaos"/>,
/// wait for <c>downtime</c>, restart the node, advance the clock to
/// <see cref="ChaosWindow.InRecovery"/>, record the cycle, and loop.
/// The loop terminates cleanly when the supplied <see cref="CancellationToken"/>
/// fires (soak shutdown) — the surrounding <see cref="OperationCanceledException"/>
/// is swallowed because cancellation is the expected termination signal,
/// not an error.
/// </summary>
public sealed class ChaosScheduler(
    IBrokerChaos chaos,
    ChaosClock clock,
    string nodeName,
    TimeSpan interval,
    TimeSpan downtime)
{
    private readonly List<ChaosEvent> _events = [];

    public IReadOnlyList<ChaosEvent> Events => _events;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken);
                var killedAt = DateTimeOffset.UtcNow;
                clock.SetWindow(ChaosWindow.DuringChaos);
                await chaos.KillNodeAsync(nodeName, cancellationToken);

                await Task.Delay(downtime, cancellationToken);
                await chaos.RestartNodeAsync(nodeName, cancellationToken);
                var restartedAt = DateTimeOffset.UtcNow;
                clock.SetWindow(ChaosWindow.InRecovery);

                _events.Add(new ChaosEvent(killedAt, restartedAt, nodeName));
            }
        }
        catch (OperationCanceledException)
        {
            // expected when soak ends; surface as normal completion so the caller
            // can await without wrapping in its own catch
        }
    }
}
