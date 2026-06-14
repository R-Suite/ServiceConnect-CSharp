using System.Collections.Concurrent;
using ServiceConnect.Examples.StressHarness.Assertions;

namespace ServiceConnect.Examples.StressHarness.Patterns.Middleware;

/// <summary>
/// Process-wide ordering trail for the custom-filter-and-middleware driver. Each
/// pipeline stage on the receiver — BeforeConsuming filter, MessageProcessing
/// middleware enter, the matching handler, middleware exit,
/// OnConsumedSuccessfully filter — appends a marker keyed by the message's flow
/// id; the driver asserts the trail observed on the receiver is
/// <c>[before, mid-enter, handler, mid-exit, on-success]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A single instance is shared across both buses, mirroring the sharing model used
/// for <c>FlowAccounting</c> / <c>PerHandlerSignal</c> / <c>FilterTrail</c>. The
/// trail must be keyed by flow id (not bus tag) because the driver runs both
/// directions concurrently and the entire pipeline for a given direction lands on
/// the same receiver bus — keying by flow id keeps the two directions' entries
/// cleanly separated even when the dispatcher interleaves them.
/// </para>
/// <para>
/// Per-flow lists are mutated under a per-list lock taken via
/// <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
/// + <c>lock(list)</c>. The list inside <see cref="Trails"/> is therefore mutated
/// only inside that lock; the driver reads via <see cref="Snapshot(Guid)"/> which
/// returns a defensive copy under the same lock so the assertion cannot observe a
/// partial append.
/// </para>
/// </remarks>
public sealed class MiddlewareTrail : IFlowKeyedSingleton
{
    /// <summary>Per-flow ordered list of stage markers.</summary>
    public ConcurrentDictionary<Guid, List<string>> Trails { get; } = new();

    /// <summary>
    /// Drops the per-flow trail row for every id in
    /// <paramref name="completedFlowIds"/>. Ids the trail never observed are
    /// ignored.
    /// </summary>
    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds)
    {
        foreach (var id in completedFlowIds)
        {
            Trails.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Appends <paramref name="marker"/> to the trail for <paramref name="flowId"/>,
    /// allocating the per-flow list on first use. Thread-safe.
    /// </summary>
    public void Record(Guid flowId, string marker)
    {
        var list = Trails.GetOrAdd(flowId, _ => []);
        lock (list)
        {
            list.Add(marker);
        }
    }

    /// <summary>
    /// Returns an immutable snapshot of the trail for <paramref name="flowId"/>, or an
    /// empty list if the flow has no recorded markers. The snapshot is taken under the
    /// same lock used by <see cref="Record(Guid, string)"/> so the caller cannot observe
    /// a partially-mutated list.
    /// </summary>
    public IReadOnlyList<string> Snapshot(Guid flowId)
    {
        if (!Trails.TryGetValue(flowId, out var list))
        {
            return [];
        }
        lock (list)
        {
            return [.. list];
        }
    }
}
