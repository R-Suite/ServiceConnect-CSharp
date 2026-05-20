using System.Collections.Concurrent;
using ServiceConnect.Examples.StressHarness.Assertions;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Process-wide observation log for the process-manager driver. Each
/// <see cref="SagaHandler"/> stage records the post-mutation <c>Stage</c> value
/// against the saga's correlation id; the driver verifies the recorded sequence
/// is <c>[1, 2, 3]</c> in order, demonstrating the framework correlated the
/// inbound messages to the same persisted state instance and surfaced the
/// monotonic mutation across handler invocations.
/// </summary>
/// <remarks>
/// A single instance is shared across both buses. The per-correlation-id list is
/// mutated under a per-list lock taken via
/// <see cref="ConcurrentDictionary{TKey, TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
/// + <c>lock(list)</c>. Read-back via <see cref="Snapshot(Guid)"/> takes the same
/// lock so the assertion cannot observe a partial append.
/// </remarks>
public sealed class SagaObservations : IFlowKeyedSingleton
{
    /// <summary>Per-saga ordered list of post-mutation stage values.</summary>
    public ConcurrentDictionary<Guid, List<int>> Stages { get; } = new();

    /// <summary>
    /// Drops the per-correlation row for every id in
    /// <paramref name="completedFlowIds"/>. Ids never observed are ignored.
    /// </summary>
    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds)
    {
        foreach (var id in completedFlowIds)
        {
            Stages.TryRemove(id, out _);
        }
    }

    public void Record(Guid correlationId, int stage)
    {
        var list = Stages.GetOrAdd(correlationId, _ => []);
        lock (list)
        {
            list.Add(stage);
        }
    }

    public IReadOnlyList<int> Snapshot(Guid correlationId)
    {
        if (!Stages.TryGetValue(correlationId, out var list))
        {
            return [];
        }
        lock (list)
        {
            return [.. list];
        }
    }
}
