using System.Collections.Concurrent;
using ServiceConnect.Examples.StressHarness.Assertions;

namespace ServiceConnect.Examples.StressHarness.Patterns.Aggregators;

/// <summary>
/// Snapshot of one dispatched aggregator batch, captured from inside
/// <see cref="StressTelemetrySliceAggregator.ExecuteAsync"/> for the driver's
/// downstream assertion. <see cref="FlowId"/> is the per-direction stress flow id
/// the driver stamped on every item; <see cref="BusTag"/> identifies the bus the
/// aggregator executed on; <see cref="Count"/> is the size of the dispatched batch.
/// </summary>
public sealed record AggregatorBatchObservation(Guid FlowId, string BusTag, int Count);

/// <summary>
/// Process-wide observation log for the aggregator driver. Each dispatched batch
/// pushes one <see cref="AggregatorBatchObservation"/> onto the bag, and completes
/// a per-flow <see cref="TaskCompletionSource{TResult}"/> so the driver can await
/// the framework's batch flush without polling.
/// </summary>
/// <remarks>
/// The bag retains every dispatched batch — under at-least-once delivery a replay
/// could legitimately produce a second observation for the same flow id, and the
/// driver tolerates that by indexing on FlowId rather than insisting on a single
/// entry per id. The TaskCompletionSource is one-shot per flow id; the first
/// ExecuteAsync to land for that flow id wins the await, which is the behaviour
/// the driver requires (subsequent observations are inspected but no longer gate
/// the await).
/// </remarks>
public sealed class AggregatorObservations : IFlowKeyedSingleton
{
    /// <summary>Every dispatched batch observed across both buses.</summary>
    public ConcurrentBag<AggregatorBatchObservation> Batches { get; } = [];

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AggregatorBatchObservation>> _waiters = new();

    /// <summary>
    /// Returns a task that completes when the first aggregator batch for
    /// <paramref name="flowId"/> dispatches. Safe to call before or after the
    /// framework flushes; first caller wins the TCS allocation.
    /// </summary>
    public Task<AggregatorBatchObservation> AwaitBatchAsync(Guid flowId, CancellationToken cancellationToken)
    {
        var tcs = _waiters.GetOrAdd(flowId, _ => new TaskCompletionSource<AggregatorBatchObservation>(TaskCreationOptions.RunContinuationsAsynchronously));
        cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return tcs.Task;
    }

    /// <summary>
    /// Records a dispatched batch and signals any pending awaiter on that flow id.
    /// Called from <see cref="StressTelemetrySliceAggregator.ExecuteAsync"/> on
    /// every framework flush.
    /// </summary>
    public void Record(AggregatorBatchObservation observation)
    {
        Batches.Add(observation);
        var tcs = _waiters.GetOrAdd(observation.FlowId, _ => new TaskCompletionSource<AggregatorBatchObservation>(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult(observation);
    }

    // The Batches bag is intentionally NOT cleaned: ConcurrentBag has no
    // targeted removal, and per-batch payload (Guid + short bus-tag + int) is
    // small enough that the unreclaimed observations stay well inside the soak
    // budget. Only the awaiter dictionary — which is keyed by flow id and
    // therefore grows with active flows — is reclaimed here.
    /// <summary>
    /// Drops the per-flow awaiter entry for every id in
    /// <paramref name="completedFlowIds"/>. Re-awaiting a reclaimed flow id
    /// yields a fresh pending TCS so a later redelivery completes cleanly.
    /// </summary>
    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds)
    {
        foreach (var id in completedFlowIds)
        {
            _waiters.TryRemove(id, out _);
        }
    }
}
