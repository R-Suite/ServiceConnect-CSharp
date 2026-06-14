namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Contract for harness accumulators that key state by flow id. A flow-keyed
/// singleton retains per-flow entries for the lifetime of the harness process
/// unless the dispatcher reclaims them after a flow completes. Without
/// reclamation a long-running soak grows unbounded; the dispatcher invokes
/// <see cref="TryRemoveCompleted"/> once per tick with every flow id that
/// finished on that tick so each accumulator drops its per-flow row.
/// </summary>
/// <remarks>
/// Implementations must be idempotent — a flow id may be passed in twice
/// (e.g. a redelivery completes after the first reclamation pass) and the
/// second call must not throw. Implementations must also tolerate flow ids
/// they never observed, since the dispatcher does not know which accumulators
/// any given flow touched.
/// </remarks>
public interface IFlowKeyedSingleton
{
    /// <summary>
    /// Drops per-flow state for every id in <paramref name="completedFlowIds"/>.
    /// Ids the accumulator never saw are ignored. Thread-safe; the dispatcher
    /// may invoke this concurrently with the accumulator's record / read paths.
    /// </summary>
    void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds);
}
