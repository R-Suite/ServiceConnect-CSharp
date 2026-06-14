using System.Collections.Concurrent;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public sealed record AccountingSummary(
    int SentCount,
    int HandledCount,
    IReadOnlyList<Guid> MissingFlows,
    IReadOnlyList<Guid> UnexpectedFlows,
    IReadOnlyList<DuplicatedFlow> DuplicatedFlows);

/// <summary>
/// A flow whose handler fired more times than the matching send recorded. Surfaces broker
/// redelivery — under chaos this is the broker's expected behaviour after a killed handler
/// failed to ack; outside the chaos window it would be a real exactly-once finding.
/// </summary>
public sealed record DuplicatedFlow(Guid FlowId, int Expected, int Observed);

public sealed class FlowAccounting
{
    private readonly ConcurrentDictionary<Guid, int> _expected = new();
    private readonly ConcurrentDictionary<Guid, int> _observed = new();

    public void RecordSend(Guid flowId, int expectedHandlerInvocations)
    {
        _expected.AddOrUpdate(flowId, expectedHandlerInvocations, (_, existing) => existing + expectedHandlerInvocations);
    }

    public void RecordHandled(Guid flowId)
    {
        _observed.AddOrUpdate(flowId, 1, (_, n) => n + 1);
    }

    /// <summary>
    /// Drops the bookkeeping for every flow whose observed handler invocations have caught
    /// up with the expected count. Long-running loops call this once per tick so the two
    /// dictionaries stay bounded by the in-flight set rather than the lifetime-cumulative
    /// set. Flows still short of their expected fan-out are left in place so the next
    /// <see cref="Reconcile"/> still reports them as missing.
    /// </summary>
    /// <returns>
    /// The flow ids that were reclaimed in this pass. Callers (the dispatch loops) feed
    /// this list into every <see cref="IFlowKeyedSingleton.TryRemoveCompleted"/> so
    /// per-flow rows recorded against driver-side sub-flow ids (e.g. saga stage ids)
    /// are reclaimed alongside the direction-level ids the dispatcher already passes.
    /// </returns>
    public IReadOnlyList<Guid> TryRemoveCompleted()
    {
        var removed = new List<Guid>();
        foreach (var kv in _expected)
        {
            var observed = _observed.GetValueOrDefault(kv.Key, 0);
            if (observed >= kv.Value)
            {
                if (_expected.TryRemove(kv.Key, out _))
                {
                    _observed.TryRemove(kv.Key, out _);
                    removed.Add(kv.Key);
                }
            }
        }
        return removed;
    }

    public AccountingSummary Reconcile()
    {
        var missing = new List<Guid>();
        var unexpected = new List<Guid>();
        var duplicated = new List<DuplicatedFlow>();

        foreach (var kv in _expected)
        {
            var observed = _observed.GetValueOrDefault(kv.Key, 0);
            if (observed < kv.Value)
            {
                missing.Add(kv.Key);
            }
            else if (observed > kv.Value)
            {
                duplicated.Add(new DuplicatedFlow(kv.Key, kv.Value, observed));
            }
        }
        foreach (var kv in _observed)
        {
            if (!_expected.ContainsKey(kv.Key))
            {
                unexpected.Add(kv.Key);
            }
        }

        return new AccountingSummary(
            SentCount: _expected.Values.Sum(),
            HandledCount: _observed.Values.Sum(),
            MissingFlows: missing,
            UnexpectedFlows: unexpected,
            DuplicatedFlows: duplicated);
    }
}
