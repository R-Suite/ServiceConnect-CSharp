using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Assertions;

public sealed class MessageLedgerAnalyzerTests
{
    [Fact]
    public void Empty_snapshot_yields_all_zero_counts()
    {
        var analysis = MessageLedgerAnalyzer.Analyze(new LedgerSnapshot([], []));

        Assert.Equal(0, analysis.TotalPublishes);
        Assert.Equal(0, analysis.AckedPublishes);
        Assert.Equal(0, analysis.FailedPublishes);
        Assert.Equal(0, analysis.TotalConsumes);
        Assert.Equal(0, analysis.AckedAndConsumed);
        Assert.Equal(0, analysis.AckedButLost);
        Assert.Equal(0, analysis.FailedThenConsumed);
        Assert.Equal(0, analysis.FailedAndLost);
        Assert.Equal(0, analysis.PerMessageRedeliveries);
        Assert.Empty(analysis.AckedButLostSample);
        Assert.Equal(0, analysis.ConsumesWithoutPublish);
        Assert.Empty(analysis.AckedButLostByWindow);
        Assert.Empty(analysis.AckedButLostByPattern);
    }

    [Fact]
    public void Single_acked_and_consumed_message_counts_as_normal()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Acked, ChaosWindow.PreChaos)],
            [new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(5), ChaosWindow.PreChaos)]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(1, analysis.TotalPublishes);
        Assert.Equal(1, analysis.AckedAndConsumed);
        Assert.Equal(0, analysis.AckedButLost);
    }

    [Fact]
    public void Acked_but_no_consume_counts_as_acked_but_lost_and_breaks_down_by_window_and_pattern()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "streaming", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Acked, ChaosWindow.InRecovery)],
            []);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(1, analysis.AckedButLost);
        Assert.Equal(1, analysis.AckedButLostByWindow[ChaosWindow.InRecovery]);
        Assert.Equal(1, analysis.AckedButLostByPattern["streaming"]);
        Assert.Single(analysis.AckedButLostSample);
    }

    [Fact]
    public void Failed_publish_with_no_consume_counts_as_failed_and_lost()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Failed, ChaosWindow.DuringChaos)],
            []);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(1, analysis.FailedAndLost);
        Assert.Equal(0, analysis.AckedButLost);
    }

    [Fact]
    public void Failed_publish_with_consume_counts_as_failed_then_consumed()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Failed, ChaosWindow.DuringChaos)],
            [new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(5), ChaosWindow.InRecovery)]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(1, analysis.FailedThenConsumed);
        Assert.Equal(0, analysis.AckedButLost);
        Assert.Equal(0, analysis.FailedAndLost);
    }

    [Fact]
    public void Multiple_consumes_for_one_publish_increments_redeliveries_by_extras()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [new PublishRecord(msg, flow, "p2p", "alpha", t, t.AddMilliseconds(2), PublishOutcome.Acked, ChaosWindow.PreChaos)],
            [
                new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(5), ChaosWindow.PreChaos),
                new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(50), ChaosWindow.PreChaos),
                new ConsumeRecord(msg, flow, "p2p", "beta", t.AddMilliseconds(100), ChaosWindow.PreChaos),
            ]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(2, analysis.PerMessageRedeliveries);
    }

    [Fact]
    public void Consume_with_no_matching_publish_increments_consumes_without_publish()
    {
        var msg = Guid.NewGuid();
        var flow = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;
        var snapshot = new LedgerSnapshot(
            [],
            [new ConsumeRecord(msg, flow, "p2p", "beta", t, ChaosWindow.PreChaos)]);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(1, analysis.ConsumesWithoutPublish);
    }

    [Fact]
    public void Acked_but_lost_sample_caps_at_twenty_rows()
    {
        var t = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 30)
            .Select(i => new PublishRecord(
                Guid.NewGuid(), Guid.NewGuid(), "p2p", "alpha",
                t.AddMilliseconds(i), t.AddMilliseconds(i + 1),
                PublishOutcome.Acked, ChaosWindow.InRecovery))
            .ToArray();
        var snapshot = new LedgerSnapshot(rows, []);

        var analysis = MessageLedgerAnalyzer.Analyze(snapshot);

        Assert.Equal(30, analysis.AckedButLost);
        Assert.Equal(20, analysis.AckedButLostSample.Count);
        Assert.True(analysis.AckedButLostSample[0].PublishStarted <= analysis.AckedButLostSample[^1].PublishStarted);
    }
}
