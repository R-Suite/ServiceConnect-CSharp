using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Orchestrator;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using Xunit;

namespace ServiceConnect.Examples.StressHarness.Tests.Orchestrator;

public sealed class LedgeredSenderTests
{
    [Fact]
    public async Task SendAsync_stamps_MessageId_into_outbound_headers()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new SendOptions
        {
            EndPoint = "stress-b.work",
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "p2p",
            },
        };

        await wrapped.SendAsync(new P2pPing(flowId), opts);

        Assert.NotNull(inner.LastSendOptions);
        var stampedHeaders = inner.LastSendOptions!.Value.Headers;
        Assert.NotNull(stampedHeaders);
        Assert.Equal(flowId.ToString("N"), stampedHeaders![StressHeaders.FlowId]);
        Assert.Equal("alpha", stampedHeaders[StressHeaders.OriginBus]);
        Assert.Equal("p2p", stampedHeaders[StressHeaders.Pattern]);
        Assert.True(stampedHeaders.ContainsKey(StressHeaders.MessageId));
        Assert.True(Guid.TryParseExact(stampedHeaders[StressHeaders.MessageId], "N", out _));
    }

    [Fact]
    public async Task SendAsync_records_publish_acked_on_success()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.DuringChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new SendOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "beta",
                [StressHeaders.Pattern] = "p2p",
            },
        };

        await wrapped.SendAsync(new P2pPing(flowId), opts);

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        var row = snapshot.Publishes[0];
        Assert.Equal(PublishOutcome.Acked, row.Outcome);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("p2p", row.Pattern);
        Assert.Equal("beta", row.OriginBus);
        Assert.Equal(ChaosWindow.DuringChaos, row.Window);
    }

    [Fact]
    public async Task SendAsync_records_publish_failed_on_inner_throw()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.InRecovery);
        var inner = new FakeBus
        {
            SendAsyncImpl = (_, _, _) => Task.FromException(new InvalidOperationException("broker down")),
        };
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new SendOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "p2p",
            },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped.SendAsync(new P2pPing(flowId), opts));
        Assert.Equal("broker down", ex.Message);

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        var row = snapshot.Publishes[0];
        Assert.Equal(PublishOutcome.Failed, row.Outcome);
    }

    [Fact]
    public async Task PublishAsync_stamps_MessageId_and_records_acked()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PostChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new PublishOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "pubsub",
            },
        };

        await wrapped.PublishAsync(new P2pPing(flowId), opts);

        Assert.NotNull(inner.LastPublishOptions);
        var stampedHeaders = inner.LastPublishOptions!.Value.Headers;
        Assert.NotNull(stampedHeaders);
        Assert.True(stampedHeaders!.ContainsKey(StressHeaders.MessageId));
        Assert.True(Guid.TryParseExact(stampedHeaders[StressHeaders.MessageId], "N", out _));
        Assert.Equal(flowId.ToString("N"), stampedHeaders[StressHeaders.FlowId]);

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        var row = snapshot.Publishes[0];
        Assert.Equal(PublishOutcome.Acked, row.Outcome);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("pubsub", row.Pattern);
        Assert.Equal("alpha", row.OriginBus);
        Assert.Equal(ChaosWindow.PostChaos, row.Window);
    }

    [Fact]
    public async Task SendRequestAsync_stamps_MessageId_and_records_acked()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new RequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "request-reply",
            },
        };

        // The fake returns default! — the test only cares about the ledger row.
        _ = await wrapped.SendRequestAsync<P2pPing, P2pPing>(new P2pPing(flowId), opts);

        Assert.NotNull(inner.LastRequestOptions);
        var stampedHeaders = inner.LastRequestOptions!.Value.Headers;
        Assert.NotNull(stampedHeaders);
        Assert.True(stampedHeaders!.ContainsKey(StressHeaders.MessageId));
        Assert.True(Guid.TryParseExact(stampedHeaders[StressHeaders.MessageId], "N", out _));

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        var row = snapshot.Publishes[0];
        Assert.Equal(PublishOutcome.Acked, row.Outcome);
        Assert.Equal(flowId, row.FlowId);
        Assert.Equal("request-reply", row.Pattern);
        Assert.Equal("alpha", row.OriginBus);
        Assert.Equal(ChaosWindow.PreChaos, row.Window);
    }

    [Fact]
    public async Task RouteAsync_records_publish_keyed_by_correlation_id_with_routing_slip_pattern()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var correlationId = Guid.NewGuid();
        var slip = new SlipOrder(correlationId);

        await wrapped.RouteAsync(slip, ["node-a.work", "node-b.work"]);

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        var row = snapshot.Publishes[0];
        Assert.Equal(correlationId, row.MessageId);
        Assert.Equal("routing-slip", row.Pattern);
        Assert.Equal("(routeasync)", row.OriginBus);
        Assert.Equal(PublishOutcome.Acked, row.Outcome);
    }

    [Fact]
    public async Task SendToManyAsync_stamps_MessageId_and_records_one_row_per_call()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new SendOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "p2p",
            },
        };

        await wrapped.SendToManyAsync(new P2pPing(flowId), ["stress-a.work", "stress-b.work"], opts);

        Assert.NotNull(inner.LastSendToManyOptions);
        var stampedHeaders = inner.LastSendToManyOptions!.Value.Headers;
        Assert.NotNull(stampedHeaders);
        Assert.True(stampedHeaders!.ContainsKey(StressHeaders.MessageId));
        Assert.True(Guid.TryParseExact(stampedHeaders[StressHeaders.MessageId], "N", out _));

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        Assert.Equal(PublishOutcome.Acked, snapshot.Publishes[0].Outcome);
    }

    [Fact]
    public async Task SendRequestMultiAsync_stamps_MessageId_and_records_acked()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new RequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "request-reply",
            },
        };

        _ = await wrapped.SendRequestMultiAsync<P2pPing, P2pPing>(new P2pPing(flowId), opts);

        Assert.NotNull(inner.LastSendRequestMultiOptions);
        var stampedHeaders = inner.LastSendRequestMultiOptions!.Value.Headers;
        Assert.NotNull(stampedHeaders);
        Assert.True(stampedHeaders!.ContainsKey(StressHeaders.MessageId));
        Assert.True(Guid.TryParseExact(stampedHeaders[StressHeaders.MessageId], "N", out _));

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        Assert.Equal(PublishOutcome.Acked, snapshot.Publishes[0].Outcome);
    }

    [Fact]
    public async Task PublishRequestAsync_stamps_MessageId_and_records_acked()
    {
        var ledger = new MessageLedger();
        var clock = new FakeChaosClock(ChaosWindow.PreChaos);
        var inner = new FakeBus();
        var wrapped = new LedgeredSender(inner, ledger, clock);

        var flowId = Guid.NewGuid();
        var opts = new RequestOptions
        {
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StressHeaders.FlowId] = flowId.ToString("N"),
                [StressHeaders.OriginBus] = "alpha",
                [StressHeaders.Pattern] = "request-reply",
            },
        };

        await wrapped.PublishRequestAsync<P2pPing, P2pPing>(new P2pPing(flowId), onReply: _ => { }, opts);

        Assert.NotNull(inner.LastPublishRequestOptions);
        var stampedHeaders = inner.LastPublishRequestOptions!.Value.Headers;
        Assert.NotNull(stampedHeaders);
        Assert.True(stampedHeaders!.ContainsKey(StressHeaders.MessageId));
        Assert.True(Guid.TryParseExact(stampedHeaders[StressHeaders.MessageId], "N", out _));

        var snapshot = ledger.Snapshot();
        Assert.Single(snapshot.Publishes);
        Assert.Equal(PublishOutcome.Acked, snapshot.Publishes[0].Outcome);
    }

    // ---------------------------------------------------------------------------
    // Test helpers
    // ---------------------------------------------------------------------------

    private sealed class FakeChaosClock(ChaosWindow window) : IChaosClock
    {
        public ChaosWindow CurrentWindow { get; } = window;
    }

    private sealed class FakeBus : IBus
    {
        public Func<object, object?, CancellationToken, Task>? SendAsyncImpl { get; set; }
        public Func<object, object?, CancellationToken, Task>? PublishAsyncImpl { get; set; }
        public SendOptions? LastSendOptions { get; private set; }
        public PublishOptions? LastPublishOptions { get; private set; }
        public RequestOptions? LastRequestOptions { get; private set; }
        public SendOptions? LastSendToManyOptions { get; private set; }
        public RequestOptions? LastSendRequestMultiOptions { get; private set; }
        public RequestOptions? LastPublishRequestOptions { get; private set; }

        public Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
        {
            LastSendOptions = options;
            return SendAsyncImpl is null ? Task.CompletedTask : SendAsyncImpl(message!, options, cancellationToken);
        }

        public Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
        {
            LastPublishOptions = options;
            return PublishAsyncImpl is null ? Task.CompletedTask : PublishAsyncImpl(message!, options, cancellationToken);
        }

        public Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
        {
            LastSendToManyOptions = options;
            return Task.CompletedTask;
        }

        public Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message
        {
            LastRequestOptions = options;
            return Task.FromResult<TReply>(default!);
        }

        public Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message
        {
            LastSendRequestMultiOptions = options;
            return Task.FromResult<IList<TReply>>([]);
        }

        public Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
            where TRequest : Message where TReply : Message
        {
            LastPublishRequestOptions = options;
            return Task.CompletedTask;
        }

        public Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default) where T : Message => Task.CompletedTask;

        public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message => throw new NotSupportedException();

        public Task StartConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopConsumingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsConsuming => true;
        public ValueTask DisposeAsync() => default;
    }
}
