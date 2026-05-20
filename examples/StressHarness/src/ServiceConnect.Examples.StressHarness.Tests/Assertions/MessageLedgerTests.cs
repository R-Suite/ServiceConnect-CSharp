using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public sealed class MessageLedgerTests
{
    [Fact]
    public void RecordPublishStart_then_RecordPublishCompleted_appends_one_publish_row()
    {
        var ledger = new MessageLedger();
        var msgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;

        ledger.RecordPublishStart(msgId, flowId, pattern: "p2p", originBus: "alpha", started: t0, window: ChaosWindow.PreChaos);
        ledger.RecordPublishCompleted(msgId, completed: t0.AddMilliseconds(2), outcome: PublishOutcome.Acked);

        var snapshot = ledger.Snapshot();
        var row = Assert.Single(snapshot.Publishes);
        Assert.Equal(msgId, row.MessageId);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("p2p", row.Pattern);
        Assert.Equal("alpha", row.OriginBus);
        Assert.Equal(t0, row.PublishStarted);
        Assert.Equal(t0.AddMilliseconds(2), row.PublishCompleted);
        Assert.Equal(PublishOutcome.Acked, row.Outcome);
        Assert.Equal(ChaosWindow.PreChaos, row.Window);
    }

    [Fact]
    public void RecordConsume_appends_one_consume_row()
    {
        var ledger = new MessageLedger();
        var msgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        ledger.RecordConsume(msgId, flowId, pattern: "p2p", consumingBus: "beta", consumed: ts, window: ChaosWindow.InRecovery);

        var snapshot = ledger.Snapshot();
        var row = Assert.Single(snapshot.Consumes);
        Assert.Equal(msgId, row.MessageId);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("p2p", row.Pattern);
        Assert.Equal("beta", row.ConsumingBus);
        Assert.Equal(ts, row.Consumed);
        Assert.Equal(ChaosWindow.InRecovery, row.Window);
    }

    [Fact]
    public void RecordConsume_supports_multiple_rows_per_message_id()
    {
        var ledger = new MessageLedger();
        var msgId = Guid.NewGuid();
        var flowId = Guid.NewGuid();

        ledger.RecordConsume(msgId, flowId, "p2p", "alpha", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);
        ledger.RecordConsume(msgId, flowId, "p2p", "alpha", DateTimeOffset.UtcNow.AddMilliseconds(50), ChaosWindow.PreChaos);

        var snapshot = ledger.Snapshot();
        Assert.Equal(2, snapshot.Consumes.Count);
        Assert.All(snapshot.Consumes, r => Assert.Equal(msgId, r.MessageId));
    }

    [Fact]
    public void RecordPublishCompleted_without_RecordPublishStart_throws()
    {
        var ledger = new MessageLedger();
        Assert.Throws<InvalidOperationException>(
            () => ledger.RecordPublishCompleted(Guid.NewGuid(), DateTimeOffset.UtcNow, PublishOutcome.Acked));
    }

    [Fact]
    public void TryRemoveCompleted_drops_publish_and_consume_rows_for_listed_flow_ids()
    {
        var ledger = new MessageLedger();
        var keepFlow = Guid.NewGuid();
        var dropFlow = Guid.NewGuid();
        var keepMsg = Guid.NewGuid();
        var dropMsg = Guid.NewGuid();

        ledger.RecordPublishStart(keepMsg, keepFlow, "p2p", "alpha", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);
        ledger.RecordPublishCompleted(keepMsg, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        ledger.RecordConsume(keepMsg, keepFlow, "p2p", "beta", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);

        ledger.RecordPublishStart(dropMsg, dropFlow, "p2p", "alpha", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);
        ledger.RecordPublishCompleted(dropMsg, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        ledger.RecordConsume(dropMsg, dropFlow, "p2p", "beta", DateTimeOffset.UtcNow, ChaosWindow.PreChaos);

        ledger.TryRemoveCompleted([dropFlow]);

        var snapshot = ledger.Snapshot();
        var keptPublish = Assert.Single(snapshot.Publishes);
        Assert.Equal(keepFlow, keptPublish.FlowId);
        var keptConsume = Assert.Single(snapshot.Consumes);
        Assert.Equal(keepFlow, keptConsume.FlowId);
    }

    [Fact]
    public void TryRemoveCompleted_for_unseen_flow_ids_is_a_noop()
    {
        var ledger = new MessageLedger();
        var ex = Record.Exception(() => ledger.TryRemoveCompleted([Guid.NewGuid(), Guid.NewGuid()]));
        Assert.Null(ex);
    }
}
