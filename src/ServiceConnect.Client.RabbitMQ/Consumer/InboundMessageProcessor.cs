using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Per-delivery processing pulled out of <see cref="RabbitMqConsumerHost"/>: builds the
/// dispatch headers, calls the bus-supplied handler delegate, and routes the result to the
/// retry queue, the terminal-failure path, or the audit publisher. Stateless apart from
/// configuration captured at construction; the host owns admission control, ack/nack
/// emission, channel lifecycle, and the shutdown signals (passed in as delegates).
/// </summary>
internal sealed class InboundMessageProcessor(
    ConsumerEventHandler? consumerEventHandler,
    MessageRetryHandler retryHandler,
    MessageAuditPublisher auditPublisher,
    IQueueConfiguration queueConfiguration,
    TimeProvider timeProvider,
    ILogger logger,
    string retryQueueName,
    bool errorsDisabled,
    bool deadLetterUnhandledMessages,
    bool includeMachineNameInHeaders,
    Func<bool> shutdownTimedOut,
    Func<CancellationToken> shutdownPublishToken)
{
    private readonly ConsumerEventHandler? _consumerEventHandler = consumerEventHandler;
    private readonly MessageRetryHandler _retryHandler = retryHandler;
    private readonly MessageAuditPublisher _auditPublisher = auditPublisher;
    private readonly IQueueConfiguration _queueConfiguration = queueConfiguration;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger _logger = logger;
    private readonly string _retryQueueName = retryQueueName;
    private readonly bool _errorsDisabled = errorsDisabled;
    private readonly bool _deadLetterUnhandledMessages = deadLetterUnhandledMessages;
    private readonly bool _includeMachineNameInHeaders = includeMachineNameInHeaders;
    private readonly Func<bool> _shutdownTimedOut = shutdownTimedOut;
    private readonly Func<CancellationToken> _shutdownPublishToken = shutdownPublishToken;

    /// <summary>
    /// Processes a single inbound delivery. Returns true if the host should ack the message
    /// (handler succeeded, or the failure was published to the retry/terminal/error path);
    /// false if the message must be nacked back to the broker (shutdown grace expired before
    /// the failure-routing publish completed).
    /// </summary>
    /// <param name="publishChannel">The dedicated publish channel for retry/audit/error republishes.</param>
    /// <param name="args">The raw RabbitMQ delivery event args.</param>
    /// <param name="copiedHeaders">
    /// The pre-built inbound headers dict. When supplied (the production path), the host has
    /// already eagerly-decoded byte[] values and stamped pre-size headroom; this method
    /// stamps additional framework headers (Redelivered, TimeReceived, DestinationAddress)
    /// in-place. When <see langword="null"/> (unit tests), the dict is built locally.
    /// </param>
    /// <param name="cancellationToken">The per-delivery cancellation token.</param>
    public async Task<bool> ProcessAsync(IChannel publishChannel, BasicDeliverEventArgs args, Dictionary<string, object>? copiedHeaders, CancellationToken cancellationToken)
    {
        ConsumeEventResult result;
        // Pre-size to incoming header count plus 3 consumer-added entries to avoid rehashes.
        // Ordinal comparer matches AMQP's case-sensitive wire contract: a sender that writes
        // "X-Trace-Id" reads it back exactly. User filters / middleware look up by string literal.
        var headers = copiedHeaders ?? CopyInboundHeadersWithEagerDecode(args);

        if (args.Redelivered)
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.Redelivered, true);
        }

        try
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeReceived, FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime));
            if (_includeMachineNameInHeaders)
            {
                HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationMachine, Environment.MachineName);
            }

            HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationAddress, _queueConfiguration.QueueName);

            // Prefer FullTypeName; fall back to TypeName. Use TryGetValue to avoid KeyNotFoundException.
            // Admission already guarantees at least one is present with a non-null value, but
            // FullTypeName could be null-valued while TypeName is valid — check the value.
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw) || typeNameRaw is null)
            {
                headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
            }

            string typeName = HeaderDecoder.Decode(typeNameRaw) ?? "";

            if (_consumerEventHandler == null)
            {
                _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
                result = new ConsumeEventResult { Success = false, Exception = new InvalidOperationException("Consumer event handler not set; message could not be dispatched.") };
            }
            else
            {
                result = await _consumerEventHandler(args.Body, typeName, headers, cancellationToken).ConfigureAwait(false);
            }

            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeProcessed, FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative cancellation by a well-behaved handler that observed the supplied CT
            // (e.g. shutdown grace cancelled _deliveryCts before the drain wait) is NOT a
            // retry-worthy handler failure. Propagate so the outer dispatch leaves the message
            // unacked for broker redelivery — without this filter the broad catch below would
            // burn a retry slot, increment the retry counter, and after max retries route the
            // message to the error exchange even though the handler never rejected it.
            throw;
        }
        catch (Exception ex)
        {
            result = new ConsumeEventResult { Exception = ex, Success = false };
        }

        var shutdownToken = _shutdownPublishToken();

        if (!result.Success)
        {
            if (_shutdownTimedOut())
            {
                return false;
            }

            // Terminal failure (permanently malformed payload — JsonException, NotSupportedException
            // from the dispatcher's deserialise path) bypasses the retry queue and goes straight to
            // the error exchange. Retrying a poison payload produces the identical failure on every
            // attempt; the retry budget would be burned for no benefit and amplify load 3× on
            // pathological inputs. Handler-thrown exceptions stay on the retry path — those reflect
            // downstream dependencies that may recover.
            if (result.TerminalFailure)
            {
                await HandleTerminalFailureDirectAsync(publishChannel, args, headers, result.Exception, shutdownToken).ConfigureAwait(false);
            }
            else
            {
                await HandleHandlerFailureAsync(publishChannel, args, headers, result.Exception, shutdownToken).ConfigureAwait(false);
            }
        }
        else if (result.NotHandled && _deadLetterUnhandledMessages && !_errorsDisabled)
        {
            if (_shutdownTimedOut())
            {
                return false;
            }

            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw) || typeNameRaw is null)
            {
                headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
            }

            var typeName = HeaderDecoder.Decode(typeNameRaw) ?? "<unknown>";

            try
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel,
                    args,
                    headers,
                    new InvalidOperationException($"No processor handled message of type '{typeName}'."),
                    shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // Non-ACE channel interruption (e.g. broker-initiated 404/406 surfacing as the
                // base type) — propagate so the outer dispatch nacks-with-requeue. The generic
                // catch below is for permanent topology faults (unroutable mandatory publish,
                // serialization drift); a torn channel must not be classified as permanent.
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
            {
                throw;
            }
            catch (Exception terminalEx)
            {
                _logger.LogError(terminalEx,
                    "Terminal-failure publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
            }
        }
        else
        {
            // Audit is orthogonal to _errorsDisabled — disabling the error/retry/DLQ topology
            // must not also disable audit, which is gated separately by
            // IQueueConfiguration.AuditingEnabled inside MessageAuditPublisher. The previous
            // chain combined the two and silently acked successful messages whenever errors
            // were disabled, losing observability without any operator signal.
            if (_shutdownTimedOut())
            {
                return false;
            }

            await PublishAuditWithDropMetricAsync(publishChannel, args, headers, shutdownToken).ConfigureAwait(false);
        }

        return !_shutdownTimedOut();
    }

    // Routes a permanently-malformed delivery (set via ConsumeEventResult.TerminalFailure)
    // directly to the error exchange, bypassing the retry queue. Transport-class exceptions
    // and shutdown-grace-expired OCE re-throw so the outer dispatch nacks-with-requeue and
    // the broker redelivers after reconnect. Non-transport publish failures (PublishException
    // on a missing error exchange, topology drift) are last-resort dropped with a log to
    // prevent unbounded redelivery on a permanently-broken topology.
    //
    // Note the asymmetry with RabbitMqHeaderValidator.SafePublishTerminalAsync, which swallows
    // ALL broker faults (AlreadyClosedException, OperationInterruptedException,
    // BrokerUnreachableException, and PublishException). That path handles header-invalid
    // messages that can never become valid; they must be acked-and-dropped even when the
    // broker is unhealthy to prevent unbounded redelivery of permanently-invalid payloads.
    // This path handles handler failures, where the message content is potentially valid and
    // redelivery is the correct outcome once the channel recovers — so broker faults propagate
    // to trigger the caller's nack-with-requeue path.
    private async Task HandleTerminalFailureDirectAsync(
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception? terminalException,
        CancellationToken shutdownToken)
    {
        try
        {
            await _retryHandler.HandleTerminalFailureAsync(
                publishChannel,
                args,
                headers,
                terminalException ?? new InvalidOperationException("Permanently invalid payload."),
                shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
        {
            throw;
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            // Non-ACE channel interruption (e.g. broker-initiated 404/406 surfacing as the
            // base type) — propagate so the outer dispatch nacks-with-requeue. The generic
            // catch below is for permanent topology faults (unroutable mandatory publish,
            // serialization drift); a torn channel must not be classified as permanent.
            throw;
        }
        catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
        {
            throw;
        }
        catch (Exception terminalEx)
        {
            _logger.LogError(terminalEx,
                "Terminal-failure publish for permanently-invalid payload failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
        }
    }

    // Extracted from ProcessAsync. Routes a handler-failed delivery through the retry queue
    // (HandleFailureAsync); on retry-publish failure tries the error exchange as a fallback
    // before giving up. Transport-class exceptions and shutdown-grace-expired OCE re-throw so
    // the outer dispatch nacks-with-requeue and the broker redelivers after reconnect.
    // Non-transport publish failures (typically PublishException on mandatory:true unroutable,
    // or topology drift) are last-resort dropped with a counter to prevent unbounded
    // redelivery on a permanently-broken topology.
    private async Task HandleHandlerFailureAsync(
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        Exception? handlerException,
        CancellationToken shutdownToken)
    {
        try
        {
            await _retryHandler.HandleFailureAsync(
                publishChannel,
                _retryQueueName,
                args,
                headers,
                handlerException,
                shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
        {
            throw;
        }
        catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
        {
            // Non-ACE channel interruption (e.g. broker-initiated 404/406 surfacing as the
            // base type) — propagate so the outer dispatch nacks-with-requeue. The generic
            // catch below is for permanent topology faults (unroutable mandatory publish,
            // serialization drift); a torn channel must not be classified as permanent.
            throw;
        }
        catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
        {
            throw;
        }
        catch (Exception retryEx)
        {
            _logger.LogError(retryEx,
                "Retry publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; attempting error-exchange fallback before drop.",
                args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
            try
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel,
                    args,
                    headers,
                    // Use the original handler exception so the DLQ Exception header identifies
                    // the actual handler failure. retryEx is logged separately above so operators
                    // still see why the retry path failed.
                    handlerException ?? retryEx,
                    shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.OperationInterruptedException)
            {
                // Non-ACE channel interruption — propagate so the outer dispatch
                // nacks-with-requeue. A torn channel must not be classified as a
                // permanent fallback failure.
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
            {
                throw;
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx,
                    "Error-exchange fallback also failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
                ServiceConnectMeter.AddRetryDrop(new TagList
                {
                    { "messaging.system", "rabbitmq" },
                    { "messaging.destination.name", _queueConfiguration.QueueName },
                    { "error.type", ExceptionTypeMapper.Map(fallbackEx) },
                });
            }
        }
    }

    // Extracted from ProcessAsync to keep the dispatch method under the analyzer's
    // length budget. Audit publish failures must not fail message delivery — audit is
    // an observability side-effect, not part of the business transaction. A throw
    // here would bubble out of ProcessAsync, leave `processed` false in EventAsync,
    // and the already-handled message would be nacked with requeue:true → duplicate
    // handler invocation.
    private async Task PublishAuditWithDropMetricAsync(
        IChannel publishChannel,
        BasicDeliverEventArgs args,
        Dictionary<string, object> headers,
        CancellationToken shutdownToken)
    {
        try
        {
            await _auditPublisher.PublishAuditIfEnabledAsync(publishChannel, args, headers, shutdownToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Audit is fire-and-forget; shutdown cancellation is expected, not an error.
            // Swallow (do NOT rethrow) so the already-handled message gets ack'd. Rethrowing
            // would leave processed=false in the caller, the outer finally nacks-with-requeue,
            // and the broker redelivers a successfully-handled message → duplicate handler
            // invocation. See learn/operations/cancellation: observability paths log Debug
            // and continue.
            _logger.LogDebug(
                "Audit publish cancelled by shutdown for delivery {DeliveryTag}; continuing to ack the original message",
                args.DeliveryTag);
        }
        // Non-cancellation failures are swallowed inside MessageAuditPublisher itself,
        // which logs at Warning and increments the messaging.serviceconnect.audit.drops
        // counter. Any exception that escapes the publisher should propagate and surface
        // as a loud nack rather than be silently swallowed twice.
    }

    // Avoid StringBuilder allocation inside DateTime.ToString("O").
    private static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }

    // Mirrors RabbitMqConsumerHost.CopyInboundHeaders: eager-decode byte[] headers so
    // HeaderDecoder.Decode hits the string fast-path on every downstream read. Pre-size
    // is +3 because ProcessAsync stamps Redelivered, TimeReceived, and DestinationAddress
    // on top of the caller's headers.
    private static Dictionary<string, object> CopyInboundHeadersWithEagerDecode(BasicDeliverEventArgs args)
    {
        var sourceHeaders = args.BasicProperties.Headers;
        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 4) + 3, StringComparer.Ordinal);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is null)
                {
                    continue;
                }
                headers[kvp.Key] = kvp.Value is byte[] bytes
                    ? Encoding.UTF8.GetString(bytes)
                    : kvp.Value;
            }
        }
        return headers;
    }

    // Test-access surface for the eager-decode helper. Delegating one-liner so the test
    // exercises the same code path ProcessAsync uses — no risk of silent drift.
    internal static Dictionary<string, object> CopyInboundHeadersForTests(BasicDeliverEventArgs args)
        => CopyInboundHeadersWithEagerDecode(args);
}
