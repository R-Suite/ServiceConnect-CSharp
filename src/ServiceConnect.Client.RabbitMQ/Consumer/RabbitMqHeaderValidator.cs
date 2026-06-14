using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Pre-dispatch validation for inbound RabbitMQ deliveries. Rejects malformed or oversized
/// messages by routing them through <see cref="IMessageRetryHandler.HandleTerminalFailureAsync"/>
/// before they reach the dispatch pipeline. Permanently-invalid messages are acked by the host
/// after rejection (not redelivered), since redelivery would just hit the same validation.
/// </summary>
internal sealed class RabbitMqHeaderValidator(
    IMessageRetryHandler retryHandler,
    long maxInboundMessageSize,
    int maxHeaderCount,
    int maxHeaderValueBytes,
    Func<CancellationToken> shutdownPublishTokenFactory,
    ILogger logger)
{
    // AMQP 0-9-1 tables in practice nest only a handful of levels deep; 32 is generous
    // and prevents adversarially-crafted sparse deep chains from exhausting the thread
    // stack (1 MB default; ~150 bytes/frame). The budget short-circuit fires first on
    // typical payloads — this guard activates only on pathologically deep nesting.
    private const int MaxNestingDepth = 32;

    private readonly IMessageRetryHandler _retryHandler = retryHandler ?? throw new ArgumentNullException(nameof(retryHandler));
    private readonly long _maxInboundMessageSize = maxInboundMessageSize;
    private readonly int _maxHeaderCount = maxHeaderCount;
    private readonly int _maxHeaderValueBytes = maxHeaderValueBytes;
    private readonly Func<CancellationToken> _shutdownPublishTokenFactory = shutdownPublishTokenFactory ?? throw new ArgumentNullException(nameof(shutdownPublishTokenFactory));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Validates a delivery against the four pre-dispatch rules. On the first failing rule,
    /// routes the message to the terminal-failure path and returns a Reject result. The host
    /// treats a Reject as "permanently invalid" — the broker delivery is acked rather than
    /// nacked-with-requeue, since redelivery would just hit the same rule.
    /// </summary>
    public async Task<HeaderValidationResult> ValidateAsync(
        BasicDeliverEventArgs args,
        IChannel publishChannel,
        Dictionary<string, object> copiedHeaders,
        CancellationToken cancellationToken)
    {
        // Pre-cancel without doing any publish I/O if the delivery has already been cancelled.
        // The terminal-failure publish itself is bounded by the shutdown-publish token (see
        // _shutdownPublishTokenFactory) so dispose-time pending publishes are still abandoned
        // even if the delivery token has not yet fired.
        cancellationToken.ThrowIfCancellationRequested();

        // ContainsKey admits a key whose value is null; use TryGetValue+non-null instead.
        // A null-valued TypeName passes ContainsKey but CopyInboundHeaders skips null values,
        // so the dispatch-site indexer would throw KeyNotFoundException and burn a retry cycle
        // on a guaranteed-fail dispatch. Reject at admission instead.
        static bool HasNonNullValue(IDictionary<string, object?> h, string key)
            => h.TryGetValue(key, out var v) && v is not null;

        // Rule 1: oversized body. This runs FIRST so subsequent rules (which copy args.Body
        // to the error exchange via HandleTerminalFailureAsync → PublishErrorAsync) cannot be
        // tricked into copying an adversarial multi-MB body. Without this ordering, a flood
        // of "oversized + missing type-name" messages would each cause a full-body publish via
        // the missing-type-name rule before the size cap was consulted, defeating
        // _maxInboundMessageSize as a DoS mitigation.
        if (args.Body.Length > _maxInboundMessageSize)
        {
            return await SafePublishTerminalAsync(
                publishChannel,
                args,
                copiedHeaders,
                new InvalidOperationException(
                    $"Inbound message size {args.Body.Length} bytes exceeds configured limit {_maxInboundMessageSize} bytes."),
                "oversized body").ConfigureAwait(false);
        }

        // Rule 2: missing type-name header. Body size is now known to be within the cap, so
        // HandleTerminalFailureAsync can safely publish args.Body to the error exchange.
        if (args.BasicProperties.Headers == null ||
            (!HasNonNullValue(args.BasicProperties.Headers, HeaderKeys.TypeName) &&
             !HasNonNullValue(args.BasicProperties.Headers, HeaderKeys.FullTypeName)))
        {
            return await SafePublishTerminalAsync(
                publishChannel,
                args,
                copiedHeaders,
                new InvalidOperationException("Message headers must contain type name."),
                "missing type-name header").ConfigureAwait(false);
        }

        // Rule 3: too many headers
        var inboundHeaders = args.BasicProperties.Headers;
        if (inboundHeaders != null && inboundHeaders.Count > _maxHeaderCount)
        {
            return await SafePublishTerminalAsync(
                publishChannel,
                args,
                copiedHeaders,
                new InvalidOperationException(
                    $"Inbound header count {inboundHeaders.Count} exceeds configured limit {_maxHeaderCount}."),
                "too many headers").ConfigureAwait(false);
        }

        // Rule 4: oversized individual header value (recursive — descends into AMQP nested
        // tables and arrays). A header value can be an AMQP nested table or array carrying
        // arbitrary payload; without descent, a single top-level header bypasses the per-value
        // byte cap entirely. The helper returns null as a sentinel for "exceeded budget
        // mid-descent" so callers short-circuit without computing the full size of an
        // adversarial payload.
        //
        // Rule 5 (aggregate): each header value individually fits the per-value cap, but the
        // sum of all values is also bounded by _maxInboundMessageSize. Without this an
        // adversary could pack MaxHeaderCount × MaxHeaderValueBytes (default 64 × 8 KiB =
        // 512 KiB) into headers and bypass the body cap entirely. Capping the aggregate at
        // the body cap keeps header capacity proportional to body capacity.
        if (inboundHeaders != null)
        {
            long aggregate = 0;
            foreach (var kvp in inboundHeaders)
            {
                // Count the key bytes too — without this, an adversary can pack
                // _maxHeaderCount keys at AMQP shortstr max length (255 bytes each)
                // and bypass roughly _maxHeaderCount × 255 bytes of "free" header
                // weight against the message-size budget.
                aggregate += Encoding.UTF8.GetByteCount(kvp.Key);
                if (aggregate > _maxInboundMessageSize)
                {
                    return await SafePublishTerminalAsync(
                        publishChannel,
                        args,
                        copiedHeaders,
                        new InvalidOperationException(
                            $"Inbound header aggregate size {aggregate} bytes exceeds the message-size budget of {_maxInboundMessageSize} bytes."),
                        "oversized header aggregate").ConfigureAwait(false);
                }

                var cost = ComputeHeaderValueByteCost(kvp.Value, _maxHeaderValueBytes);
                if (cost is null)
                {
                    return await SafePublishTerminalAsync(
                        publishChannel,
                        args,
                        copiedHeaders,
                        new InvalidOperationException(
                            $"Inbound header '{kvp.Key}' exceeds configured per-value limit {_maxHeaderValueBytes} bytes (or its nested AMQP table/array does)."),
                        "oversized header value").ConfigureAwait(false);
                }
                aggregate += cost.Value;
                if (aggregate > _maxInboundMessageSize)
                {
                    return await SafePublishTerminalAsync(
                        publishChannel,
                        args,
                        copiedHeaders,
                        new InvalidOperationException(
                            $"Inbound header aggregate size {aggregate} bytes exceeds the message-size budget of {_maxInboundMessageSize} bytes."),
                        "oversized header aggregate").ConfigureAwait(false);
                }
            }
        }

        return HeaderValidationResult.Accept();
    }

    /// <summary>
    /// Attempts to publish a terminal failure to the error exchange, then returns a
    /// <see cref="HeaderValidationResult.Reject"/> regardless of whether the publish
    /// succeeded. Broker exceptions (<see cref="global::RabbitMQ.Client.Exceptions.AlreadyClosedException"/>,
    /// <see cref="global::RabbitMQ.Client.Exceptions.OperationInterruptedException"/>,
    /// <see cref="global::RabbitMQ.Client.Exceptions.BrokerUnreachableException"/>,
    /// <see cref="global::RabbitMQ.Client.Exceptions.PublishException"/>) are swallowed
    /// with an error log so that a closed or unroutable publish channel does not prevent the
    /// caller from acking the inbound delivery. The message is permanently invalid regardless
    /// of whether the error-exchange publish succeeds; requeuing it would loop the same rule.
    /// <see cref="OperationCanceledException"/> is re-thrown so cooperative shutdown is
    /// distinguishable from a broker failure.
    /// </summary>
    private async Task<HeaderValidationResult> SafePublishTerminalAsync(
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> copiedHeaders,
        Exception failure,
        string rejectReason)
    {
        try
        {
            await _retryHandler.HandleTerminalFailureAsync(
                publishChannel, args, copiedHeaders, failure, _shutdownPublishTokenFactory()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            // AlreadyClosedException derives from OperationInterruptedException, so the
            // OperationInterruptedException arm already covers it. Both are listed explicitly
            // as a documentation aid: readers scanning for "what happens when the channel
            // is closed?" find the answer without consulting the RabbitMQ.Client type hierarchy.
            ex is global::RabbitMQ.Client.Exceptions.AlreadyClosedException
                or global::RabbitMQ.Client.Exceptions.OperationInterruptedException
                or global::RabbitMQ.Client.Exceptions.BrokerUnreachableException
                or global::RabbitMQ.Client.Exceptions.PublishException)
        {
            // Publish channel is unhealthy or the error exchange is unroutable (topology drift).
            // Swallow so the caller still receives a Reject and acks the original delivery:
            // the message is permanently invalid (it failed header validation), so requeuing
            // for redelivery while the broker is degraded would loop the same message
            // indefinitely. The error queue publish is the best-effort observability path;
            // a closed or unroutable publish channel means the message is dropped but the
            // ack still removes it from the inbound queue, matching the
            // IQueueConfiguration.DisableErrors contract shape.
            _logger.LogError(ex,
                "RabbitMqHeaderValidator could not publish terminal failure to error exchange ({Reason}); dropping the inbound message after ack.",
                rejectReason);
        }
        return HeaderValidationResult.Reject(rejectReason);
    }

    /// <summary>
    /// Computes the approximate byte cost of an AMQP header value. Returns the sum of
    /// scalar string/byte[] costs; descends into nested <see cref="System.Collections.IDictionary"/>
    /// and <see cref="System.Collections.IList"/> values. Returns <c>null</c> as a sentinel for
    /// "exceeded <paramref name="budget"/> mid-descent" so callers can short-circuit without
    /// computing the full size of an adversarial payload.
    /// </summary>
    private static int? ComputeHeaderValueByteCost(object? value, int budget, int depthRemaining = MaxNestingDepth)
    {
        // Reject adversarial sparse deep chains: 32 levels is far beyond AMQP norms.
        if (depthRemaining <= 0)
        {
            return null;
        }

        // Per-entry framing overhead estimate: AMQP table entries carry a 1-byte type tag
        // plus the entry key length. Round up by 4 bytes to keep the cost-bound conservative
        // without parsing the full AMQP frame.
        const int PerEntryOverhead = 4;

        return value switch
        {
            null => 0,
            // byte[] arm before IList arm — byte[] implements IList in C#, so the more-specific
            // pattern must come first or all byte[] values would route through the array path.
            byte[] b => b.Length > budget ? null : b.Length,
            string s => StringByteCostOrNull(s, budget),
            // Nested AMQP table: descend through the values; framework string keys do not
            // contribute to the value-byte count (they're bounded by header name conventions).
            System.Collections.IDictionary dict => SumDictionaryCost(dict, budget, depthRemaining),
            // Nested AMQP array: descend through the elements.
            System.Collections.IList list => SumListCost(list, budget, depthRemaining),
            // Scalars (int, bool, DateTime, etc.) are size-bounded by their type.
            _ => 0,
        };

        static int? StringByteCostOrNull(string s, int budget)
        {
            var size = Encoding.UTF8.GetByteCount(s);
            return size > budget ? null : size;
        }

        static int? SumDictionaryCost(System.Collections.IDictionary d, int budget, int depthRemaining)
        {
            var running = 0;
            foreach (System.Collections.DictionaryEntry entry in d)
            {
                running += PerEntryOverhead;
                if (running > budget) { return null; }
                var nested = ComputeHeaderValueByteCost(entry.Value, budget - running, depthRemaining - 1);
                if (nested is null) { return null; }
                running += nested.Value;
                if (running > budget) { return null; }
            }
            return running;
        }

        static int? SumListCost(System.Collections.IList list, int budget, int depthRemaining)
        {
            var running = 0;
            foreach (var item in list)
            {
                running += PerEntryOverhead;
                if (running > budget) { return null; }
                var nested = ComputeHeaderValueByteCost(item, budget - running, depthRemaining - 1);
                if (nested is null) { return null; }
                running += nested.Value;
                if (running > budget) { return null; }
            }
            return running;
        }
    }
}
