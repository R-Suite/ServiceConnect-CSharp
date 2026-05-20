using System.Collections.Concurrent;
using ServiceConnect.Examples.StressHarness.Chaos;

namespace ServiceConnect.Examples.StressHarness.Assertions;

/// <summary>
/// Per-message publish + consume accumulator. The harness wraps every header-bearing
/// publish surface (<c>LedgeredSender</c>) and records one publish row;
/// inbound handlers call <see cref="RecordConsume"/> once per dispatch. The post-run
/// <c>MessageLedgerAnalyzer</c> cross-references publishes and consumes by
/// <see cref="PublishRecord.MessageId"/> to classify each publish into one of four
/// quadrants — see the spec for the diagnostic intent.
/// </summary>
/// <remarks>
/// Thread-safe; concurrent recorders allowed. Implements
/// <see cref="IFlowKeyedSingleton"/> so the dispatcher can reclaim per-flow rows
/// after each tick — the same memory-bounding pattern used by the other flow-keyed
/// singletons. Failed and unmatched publishes are never reclaimed: their
/// detail must survive into the report.
/// </remarks>
public sealed class MessageLedger : IFlowKeyedSingleton
{
    private readonly ConcurrentDictionary<Guid, PublishState> _publishes = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<ConsumeRecord>> _consumes = new();

    private sealed record PublishState(
        Guid FlowId,
        string Pattern,
        string OriginBus,
        DateTimeOffset Started,
        ChaosWindow Window,
        DateTimeOffset? Completed,
        PublishOutcome? Outcome);

    public void RecordPublishStart(
        Guid messageId,
        Guid flowId,
        string pattern,
        string originBus,
        DateTimeOffset started,
        ChaosWindow window)
    {
        _publishes[messageId] = new PublishState(flowId, pattern, originBus, started, window, Completed: null, Outcome: null);
    }

    public void RecordPublishCompleted(Guid messageId, DateTimeOffset completed, PublishOutcome outcome)
    {
        _publishes.AddOrUpdate(
            messageId,
            addValueFactory: _ => throw new InvalidOperationException(
                $"RecordPublishCompleted called for unknown MessageId {messageId:N}; RecordPublishStart must precede it."),
            updateValueFactory: (_, prev) => prev with { Completed = completed, Outcome = outcome });
    }

    public void RecordConsume(
        Guid messageId,
        Guid flowId,
        string pattern,
        string consumingBus,
        DateTimeOffset consumed,
        ChaosWindow window)
    {
        var bag = _consumes.GetOrAdd(messageId, _ => []);
        bag.Add(new ConsumeRecord(messageId, flowId, pattern, consumingBus, consumed, window));
    }

    public LedgerSnapshot Snapshot()
    {
        var publishes = new List<PublishRecord>(_publishes.Count);
        foreach (var (messageId, state) in _publishes)
        {
            if (state.Completed is null || state.Outcome is null)
            {
                continue;
            }
            publishes.Add(new PublishRecord(
                messageId,
                state.FlowId,
                state.Pattern,
                state.OriginBus,
                state.Started,
                state.Completed.Value,
                state.Outcome.Value,
                state.Window));
        }

        var consumes = new List<ConsumeRecord>(_consumes.Count);
        foreach (var (_, bag) in _consumes)
        {
            consumes.AddRange(bag);
        }

        return new LedgerSnapshot(publishes, consumes);
    }

    public void TryRemoveCompleted(IEnumerable<Guid> completedFlowIds)
    {
        var completedSet = new HashSet<Guid>(completedFlowIds);
        if (completedSet.Count == 0)
        {
            return;
        }

        foreach (var (messageId, state) in _publishes)
        {
            if (completedSet.Contains(state.FlowId))
            {
                _publishes.TryRemove(messageId, out _);
            }
        }

        foreach (var (messageId, bag) in _consumes)
        {
            if (bag.IsEmpty)
            {
                continue;
            }
            var sampleFlowId = bag.First().FlowId;
            if (completedSet.Contains(sampleFlowId))
            {
                _consumes.TryRemove(messageId, out _);
            }
        }
    }
}
