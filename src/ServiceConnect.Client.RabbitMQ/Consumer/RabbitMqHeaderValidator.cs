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

        // Rule 4: oversized individual header value
        if (inboundHeaders != null)
        {
            foreach (var kvp in inboundHeaders)
            {
                int byteSize;
                if (kvp.Value is byte[] bytes)
                {
                    byteSize = bytes.Length;
                }
                else if (kvp.Value is string s)
                {
                    // string headers stamped by ServiceConnect (TypeName, FullTypeName, etc.) need
                    // bounding too — a buggy producer could send a 100MB string and exhaust memory
                    // on every consumer in the system. UTF-8 byte count matches the on-wire size.
                    byteSize = Encoding.UTF8.GetByteCount(s);
                }
                else
                {
                    // Non-string, non-byte-array headers (int, bool, etc.) are size-bounded by their type.
                    continue;
                }

                if (byteSize > _maxHeaderValueBytes)
                {
                    await _retryHandler.HandleTerminalFailureAsync(
                        publishChannel,
                        args,
                        copiedHeaders,
                        new InvalidOperationException(
                            $"Inbound header '{kvp.Key}' size {byteSize} bytes exceeds configured limit {_maxHeaderValueBytes} bytes."),
                        _shutdownPublishTokenFactory()).ConfigureAwait(false);
                    return HeaderValidationResult.Reject("oversized header value");
                }
            }
        }

        return HeaderValidationResult.Accept();
    }
}
