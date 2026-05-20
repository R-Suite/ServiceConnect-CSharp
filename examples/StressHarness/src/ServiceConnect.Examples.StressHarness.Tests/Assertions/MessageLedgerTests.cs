using FluentAssertions;
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
        snapshot.Publishes.Should().ContainSingle();
        var row = snapshot.Publishes[0];
        row.MessageId.Should().Be(msgId);
        row.FlowId.Should().Be(flowId);
        row.Pattern.Should().Be("p2p");
        row.OriginBus.Should().Be("alpha");
        row.PublishStarted.Should().Be(t0);
        row.PublishCompleted.Should().Be(t0.AddMilliseconds(2));
        row.Outcome.Should().Be(PublishOutcome.Acked);
        row.Window.Should().Be(ChaosWindow.PreChaos);
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
        snapshot.Consumes.Should().ContainSingle();
        var row = snapshot.Consumes[0];
        row.MessageId.Should().Be(msgId);
        row.FlowId.Should().Be(flowId);
        row.Pattern.Should().Be("p2p");
        row.ConsumingBus.Should().Be("beta");
        row.Consumed.Should().Be(ts);
        row.Window.Should().Be(ChaosWindow.InRecovery);
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
        snapshot.Consumes.Count.Should().Be(2);
        snapshot.Consumes.Should().AllSatisfy(r => r.MessageId.Should().Be(msgId));
    }

    [Fact]
    public void RecordPublishCompleted_without_RecordPublishStart_throws()
    {
        var ledger = new MessageLedger();
        var act = () => ledger.RecordPublishCompleted(Guid.NewGuid(), DateTimeOffset.UtcNow, PublishOutcome.Acked);
        act.Should().Throw<InvalidOperationException>();
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
        snapshot.Publishes.Should().ContainSingle().Which.FlowId.Should().Be(keepFlow);
        snapshot.Consumes.Should().ContainSingle().Which.FlowId.Should().Be(keepFlow);
    }

    [Fact]
    public void TryRemoveCompleted_for_unseen_flow_ids_is_a_noop()
    {
        var ledger = new MessageLedger();
        var act = () => ledger.TryRemoveCompleted([Guid.NewGuid(), Guid.NewGuid()]);
        act.Should().NotThrow();
    }
}
