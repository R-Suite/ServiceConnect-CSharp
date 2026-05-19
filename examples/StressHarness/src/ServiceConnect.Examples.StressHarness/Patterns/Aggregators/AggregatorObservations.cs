using System.Collections.Concurrent;

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
public sealed class AggregatorObservations
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
}
