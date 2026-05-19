using System.Collections.Concurrent;

namespace ServiceConnect.Examples.StressHarness.Assertions;

public sealed record AccountingSummary(
    int SentCount,
    int HandledCount,
    IReadOnlyList<Guid> MissingFlows,
    IReadOnlyList<Guid> UnexpectedFlows);

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

    public AccountingSummary Reconcile()
    {
        var missing = new List<Guid>();
        var unexpected = new List<Guid>();

        foreach (var kv in _expected)
        {
            var observed = _observed.GetValueOrDefault(kv.Key, 0);
            if (observed < kv.Value)
            {
                missing.Add(kv.Key);
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
            UnexpectedFlows: unexpected);
    }
}
