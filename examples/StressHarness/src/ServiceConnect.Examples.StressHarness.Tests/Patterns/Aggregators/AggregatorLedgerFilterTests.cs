using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Examples.StressHarness.Patterns.Aggregators;
using ServiceConnect.Interfaces;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Patterns.Aggregators;

public sealed class AggregatorLedgerFilterTests
{
    [Fact]
    public async Task ProcessAsync_aggregator_envelope_with_stress_headers_records_consume()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.InRecovery);
        var filter = new AggregatorLedgerFilter("alpha", ledger, clock);

        var messageId = Guid.NewGuid();
        var flowId = Guid.NewGuid();
        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [StressHeaders.Pattern] = "aggregator",
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.MessageId] = messageId.ToString("N"),
            },
        };

        var action = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, action);
        var snapshot = ledger.Snapshot();
        var row = Assert.Single(snapshot.Consumes);
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("aggregator", row.Pattern);
        Assert.Equal("alpha", row.ConsumingBus);
        Assert.Equal(ChaosWindow.InRecovery, row.Window);
    }

    [Fact]
    public async Task ProcessAsync_non_aggregator_envelope_does_not_record_consume()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var filter = new AggregatorLedgerFilter("alpha", ledger, clock);

        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [StressHeaders.Pattern] = "p2p",
                [StressHeaders.FlowId] = Guid.NewGuid().ToString("N"),
                [StressHeaders.MessageId] = Guid.NewGuid().ToString("N"),
            },
        };

        var action = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, action);
        Assert.Empty(ledger.Snapshot().Consumes);
    }

    [Fact]
    public async Task ProcessAsync_aggregator_envelope_missing_MessageId_does_not_record_and_does_not_block()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var filter = new AggregatorLedgerFilter("alpha", ledger, clock);

        var envelope = new Envelope
        {
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [StressHeaders.Pattern] = "aggregator",
                [StressHeaders.FlowId] = Guid.NewGuid().ToString("N"),
                // MessageId absent
            },
        };

        var action = await filter.ProcessAsync(envelope);

        Assert.Equal(FilterAction.Continue, action);
        Assert.Empty(ledger.Snapshot().Consumes);
    }

    private sealed class FakeChaosClock(ChaosWindow window) : IChaosClock
    {
        public ChaosWindow CurrentWindow { get; } = window;
    }
}
