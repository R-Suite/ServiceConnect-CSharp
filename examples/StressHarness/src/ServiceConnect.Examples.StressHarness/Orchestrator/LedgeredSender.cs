using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Chaos;
using ServiceConnect.Examples.StressHarness.Patterns;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Examples.StressHarness.Orchestrator;

/// <summary>
/// Decorates an <see cref="IBus"/> so every header-bearing publish surface writes a
/// publish row into the supplied <see cref="MessageLedger"/>. Stamps a fresh <c>Guid</c>
/// into the <see cref="StressHeaders.MessageId"/> header on each outbound call so the
/// receiver handler can echo the same id back via <see cref="MessageLedger.RecordConsume"/>.
/// Non-header surfaces (<see cref="IBus.RouteAsync"/>, <see cref="IBus.CreateStream"/>)
/// cannot carry caller-controlled headers; they record one row keyed by the message's
/// <see cref="Message.CorrelationId"/> so a complete-call loss is still visible — per-hop
/// or per-chunk granularity needs framework instrumentation and is out of scope.
/// </summary>
public sealed class LedgeredSender(IBus inner, MessageLedger ledger, IChaosClock clock) : IBus
{
    public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message
    {
        var (messageId, stamped) = StampSendOptions(options);
        var (flowId, pattern, originBus) = ExtractMeta(stamped.Headers, message.CorrelationId);
        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            await inner.SendAsync(message, stamped, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message
    {
        var (messageId, stamped) = StampPublishOptions(options);
        var (flowId, pattern, originBus) = ExtractMeta(stamped.Headers, message.CorrelationId);
        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            await inner.PublishAsync(message, stamped, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public async Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message
    {
        // One ledger row per call — SendToMany is one harness operation, even though the
        // inner bus fans out to N endpoints internally.
        var (messageId, stamped) = StampSendOptions(options);
        var (flowId, pattern, originBus) = ExtractMeta(stamped.Headers, message.CorrelationId);
        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            await inner.SendToManyAsync(message, endPoints, stamped, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public async Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        var (messageId, stamped) = StampRequestOptions(options);
        var (flowId, pattern, originBus) = ExtractMeta(stamped.Headers, message.CorrelationId);
        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            var reply = await inner.SendRequestAsync<TRequest, TReply>(message, stamped, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
            return reply;
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        var (messageId, stamped) = StampRequestOptions(options);
        var (flowId, pattern, originBus) = ExtractMeta(stamped.Headers, message.CorrelationId);
        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            var replies = await inner.SendRequestMultiAsync<TRequest, TReply>(message, stamped, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
            return replies;
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        var (messageId, stamped) = StampRequestOptions(options);
        var (flowId, pattern, originBus) = ExtractMeta(stamped.Headers, message.CorrelationId);
        ledger.RecordPublishStart(messageId, flowId, pattern, originBus, DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            await inner.PublishRequestAsync(message, onReply, stamped, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        }
        catch
        {
            ledger.RecordPublishCompleted(messageId, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public async Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default)
        where T : Message
    {
        // RouteAsync has no SendOptions overload — the harness cannot stamp the MessageId
        // header. Use the message's CorrelationId as the ledger key so per-call coverage
        // is preserved. Per-hop granularity needs framework instrumentation.
        var ledgerKey = message.CorrelationId;
        ledger.RecordPublishStart(ledgerKey, message.CorrelationId, pattern: "routing-slip", originBus: "(routeasync)", DateTimeOffset.UtcNow, clock.CurrentWindow);
        try
        {
            await inner.RouteAsync(message, destinations, cancellationToken).ConfigureAwait(false);
            ledger.RecordPublishCompleted(ledgerKey, DateTimeOffset.UtcNow, PublishOutcome.Acked);
        }
        catch
        {
            ledger.RecordPublishCompleted(ledgerKey, DateTimeOffset.UtcNow, PublishOutcome.Failed);
            throw;
        }
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
    {
        // Streaming chunks are framework-emitted; the wrap sees only this high-level call.
        // Recording nothing here is intentional — there is no awaitable per-chunk outcome
        // the wrap can observe. Streaming losses still surface via FlowAccounting.MissingFlows.
        return inner.CreateStream<T>(endpoint);
    }

    public Task StartConsumingAsync(CancellationToken cancellationToken = default) => inner.StartConsumingAsync(cancellationToken);
    public Task StopConsumingAsync(CancellationToken cancellationToken = default) => inner.StopConsumingAsync(cancellationToken);
    public bool IsConsuming => inner.IsConsuming;
    public bool IsCancelledByBroker => inner.IsCancelledByBroker;
    public bool IsStopped => inner.IsStopped;
    public Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default) => inner.RequestTimeoutAsync(correlationId, delay, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private (Guid MessageId, SendOptions Stamped) StampSendOptions(SendOptions? options)
    {
        var messageId = Guid.NewGuid();
        var newHeaders = MergeHeaders(options?.Headers, messageId);
        var stamped = (options ?? default) with { Headers = newHeaders };
        return (messageId, stamped);
    }

    private (Guid MessageId, PublishOptions Stamped) StampPublishOptions(PublishOptions? options)
    {
        var messageId = Guid.NewGuid();
        var newHeaders = MergeHeaders(options?.Headers, messageId);
        var stamped = (options ?? default) with { Headers = newHeaders };
        return (messageId, stamped);
    }

    private (Guid MessageId, RequestOptions Stamped) StampRequestOptions(RequestOptions? options)
    {
        var messageId = Guid.NewGuid();
        var newHeaders = MergeHeaders(options?.Headers, messageId);
        // default(RequestOptions) leaves Timeout=0 which the framework rejects; start from
        // Default (Timeout=DefaultTimeoutMs) and override only the headers.
        var baseOpts = options ?? RequestOptions.Default;
        var stamped = baseOpts with { Headers = newHeaders };
        return (messageId, stamped);
    }

    private static Dictionary<string, string> MergeHeaders(IReadOnlyDictionary<string, string>? existing, Guid messageId)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var (k, v) in existing)
            {
                merged[k] = v;
            }
        }
        merged[StressHeaders.MessageId] = messageId.ToString("N");
        return merged;
    }

    private static (Guid FlowId, string Pattern, string OriginBus) ExtractMeta(IReadOnlyDictionary<string, string>? headers, Guid correlationFallback)
    {
        var flowId = correlationFallback;
        var pattern = "(unknown)";
        var originBus = "(unknown)";
        if (headers is not null)
        {
            if (headers.TryGetValue(StressHeaders.FlowId, out var rawFlow) && Guid.TryParseExact(rawFlow, "N", out var parsedFlow))
            {
                flowId = parsedFlow;
            }
            if (headers.TryGetValue(StressHeaders.Pattern, out var rawPattern))
            {
                pattern = rawPattern;
            }
            if (headers.TryGetValue(StressHeaders.OriginBus, out var rawOrigin))
            {
                originBus = rawOrigin;
            }
        }
        return (flowId, pattern, originBus);
    }
}
