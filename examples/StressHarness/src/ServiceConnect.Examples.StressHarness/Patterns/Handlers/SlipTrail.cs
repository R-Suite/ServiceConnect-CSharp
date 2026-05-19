using System.Collections.Concurrent;

namespace ServiceConnect.Examples.StressHarness.Patterns.Handlers;

/// <summary>
/// Process-wide observation log for the routing-slip driver. Each handler invocation
/// appends the bus tag it ran on to the per-flow trail; the driver polls the trail
/// length to detect when both hops have completed and then inspects the order to
/// assert the slip visited the queues in the destinations-list order.
/// </summary>
/// <remarks>
/// <para>
/// The trail is mutated under a per-flow lock implicit in the
/// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>
/// contract — <c>AddOrUpdate</c>'s factory runs while the key's bucket is locked,
/// so concurrent appends from two near-simultaneous hops on different buses see a
/// serialised order. The driver reads the trail via <see cref="Snapshot"/> which
/// allocates a fresh array under the same lock so the assertion path observes a
/// consistent view.
/// </para>
/// <para>
/// Entries are never reclaimed for the lifetime of the harness process — flow ids
/// are cryptographic GUIDs, so memory growth is bounded by the test run's total
/// flow count.
/// </para>
/// </remarks>
public sealed class SlipTrail
{
    private readonly ConcurrentDictionary<Guid, List<string>> _trails = new();

    /// <summary>
    /// Appends <paramref name="busTag"/> to the trail for <paramref name="flowId"/>.
    /// Allocates the trail on first call for the flow id; subsequent calls append
    /// under the same per-key lock the dictionary enforces.
    /// </summary>
    public void Record(Guid flowId, string busTag)
    {
        _trails.AddOrUpdate(
            flowId,
            _ => [busTag],
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(busTag);
                }
                return existing;
            });
    }

    /// <summary>
    /// Returns a snapshot of the current trail for <paramref name="flowId"/>, or
    /// an empty list if no handler has fired yet. The snapshot is allocated under
    /// the per-key lock so a concurrent <see cref="Record"/> cannot interleave with
    /// the read.
    /// </summary>
    public IReadOnlyList<string> Snapshot(Guid flowId)
    {
        if (!_trails.TryGetValue(flowId, out var trail))
        {
            return [];
        }
        lock (trail)
        {
            return [.. trail];
        }
    }
}
