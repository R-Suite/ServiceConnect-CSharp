using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Pre-dispatch validation for inbound RabbitMQ deliveries. Rejects malformed or oversized
/// messages by routing them through <see cref="MessageRetryHandler.HandleTerminalFailureAsync"/>
/// before they reach the dispatch pipeline. Permanently-invalid messages are acked by the host
/// after rejection (not redelivered), since redelivery would just hit the same validation.
/// </summary>
internal sealed class RabbitMqHeaderValidator(
    MessageRetryHandler retryHandler,
    long maxInboundMessageSize,
    int maxHeaderCount,
    int maxHeaderValueBytes,
    Func<CancellationToken> shutdownPublishTokenFactory)
{
    // AMQP 0-9-1 tables in practice nest only a handful of levels deep; 32 is generous
    // and prevents adversarially-crafted sparse deep chains from exhausting the thread
    // stack (1 MB default; ~150 bytes/frame). The budget short-circuit fires first on
    // typical payloads — this guard activates only on pathologically deep nesting.
    private const int MaxNestingDepth = 32;

    private readonly MessageRetryHandler _retryHandler = retryHandler ?? throw new ArgumentNullException(nameof(retryHandler));
    private readonly long _maxInboundMessageSize = maxInboundMessageSize;
    private readonly int _maxHeaderCount = maxHeaderCount;
    private readonly int _maxHeaderValueBytes = maxHeaderValueBytes;
    private readonly Func<CancellationToken> _shutdownPublishTokenFactory = shutdownPublishTokenFactory ?? throw new ArgumentNullException(nameof(shutdownPublishTokenFactory));

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

        // Rule 1: missing type-name header
        if (args.BasicProperties.Headers == null ||
            (!HasNonNullValue(args.BasicProperties.Headers, HeaderKeys.TypeName) &&
             !HasNonNullValue(args.BasicProperties.Headers, HeaderKeys.FullTypeName)))
        {
            await _retryHandler.HandleTerminalFailureAsync(
                publishChannel,
                args,
                copiedHeaders,
                new InvalidOperationException("Message headers must contain type name."),
                _shutdownPublishTokenFactory()).ConfigureAwait(false);
            return HeaderValidationResult.Reject("missing type-name header");
        }

        // Rule 2: oversized body
        if (args.Body.Length > _maxInboundMessageSize)
        {
            await _retryHandler.HandleTerminalFailureAsync(
                publishChannel,
                args,
                copiedHeaders,
                new InvalidOperationException(
                    $"Inbound message size {args.Body.Length} bytes exceeds configured limit {_maxInboundMessageSize} bytes."),
                _shutdownPublishTokenFactory()).ConfigureAwait(false);
            return HeaderValidationResult.Reject("oversized body");
        }

        // Rule 3: too many headers
        var inboundHeaders = args.BasicProperties.Headers;
        if (inboundHeaders != null && inboundHeaders.Count > _maxHeaderCount)
        {
            await _retryHandler.HandleTerminalFailureAsync(
                publishChannel,
                args,
                copiedHeaders,
                new InvalidOperationException(
                    $"Inbound header count {inboundHeaders.Count} exceeds configured limit {_maxHeaderCount}."),
                _shutdownPublishTokenFactory()).ConfigureAwait(false);
            return HeaderValidationResult.Reject("too many headers");
        }

        // Rule 4: oversized individual header value (recursive — descends into AMQP nested
        // tables and arrays). A header value can be an AMQP nested table or array carrying
        // arbitrary payload; without descent, a single top-level header bypasses the per-value
        // byte cap entirely. The helper returns null as a sentinel for "exceeded budget
        // mid-descent" so callers short-circuit without computing the full size of an
        // adversarial payload.
        if (inboundHeaders != null)
        {
            foreach (var kvp in inboundHeaders)
            {
                if (ComputeHeaderValueByteCost(kvp.Value, _maxHeaderValueBytes) is null)
                {
                    await _retryHandler.HandleTerminalFailureAsync(
                        publishChannel,
                        args,
                        copiedHeaders,
                        new InvalidOperationException(
                            $"Inbound header '{kvp.Key}' exceeds configured per-value limit {_maxHeaderValueBytes} bytes (or its nested AMQP table/array does)."),
                        _shutdownPublishTokenFactory()).ConfigureAwait(false);
                    return HeaderValidationResult.Reject("oversized header value");
                }
            }
        }

        return HeaderValidationResult.Accept();
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
