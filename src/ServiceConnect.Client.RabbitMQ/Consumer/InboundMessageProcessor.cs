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
    public async Task<bool> ProcessAsync(IChannel publishChannel, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        ConsumeEventResult result;
        // Pre-size to incoming header count plus 3 consumer-added entries to avoid rehashes.
        // Ordinal comparer matches AMQP's case-sensitive wire contract: a sender that writes
        // "X-Trace-Id" reads it back exactly. User filters / middleware look up by string literal.
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
                // Mirrors RabbitMqConsumerHost.CopyInboundHeaders: eager-decode byte[] headers so
                // HeaderDecoder.Decode hits the string fast-path on every downstream read.
                headers[kvp.Key] = kvp.Value is byte[] bytes
                    ? Encoding.UTF8.GetString(bytes)
                    : kvp.Value;
            }
        }

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

            try
            {
                await _retryHandler.HandleFailureAsync(
                    publishChannel,
                    _retryQueueName,
                    args,
                    headers,
                    result.Exception,
                    shutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Shutdown grace window expired mid-publish — propagate so the outer finally
                // leaves the message unacked for broker redelivery after reconnection.
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                // See learn/operations/cancellation: transient transport failures must NOT
                // ack-and-drop messages.
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                throw;
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx,
                    "Retry publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
                // Surface the drop on a dedicated counter so operators can alert on the
                // ack-and-drop branch separately from logged errors. error.type goes through
                // the allow-list mapper to keep cardinality bounded.
                ServiceConnectMeter.AddRetryDrop(new TagList
                {
                    { "messaging.system", "rabbitmq" },
                    { "messaging.destination.name", _queueConfiguration.QueueName },
                    { "error.type", ExceptionTypeMapper.Map(retryEx) },
                });
                // Intentionally swallow: includes PublishException (mandatory:true, retry queue gone).
                // Acking now prevents the broker from redelivering into the same failed path; letting
                // this propagate would nack with requeue:true and hot-loop on a poison message.
            }
        }
        else if (result.NotHandled && _deadLetterUnhandledMessages && !_errorsDisabled)
        {
            if (_shutdownTimedOut())
            {
                return false;
            }

            // Route via the terminal-failure path (error exchange) — a message with no
            // handler is not a retryable condition, so bypass the retry queue.
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
                // Shutdown grace window expired mid-publish — propagate so the outer finally
                // leaves the message unacked for broker redelivery after reconnection.
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                // See learn/operations/cancellation.
                throw;
            }
            catch (global::RabbitMQ.Client.Exceptions.BrokerUnreachableException)
            {
                // Transport-class failure — rethrow so the outer finally nacks-with-requeue
                // and the broker redelivers after reconnect.
                throw;
            }
            catch (Exception terminalEx)
            {
                _logger.LogError(terminalEx,
                    "Terminal-failure publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
                // Intentionally swallow — same rationale as the HandleFailureAsync catch above.
                // Includes PublishException (mandatory:true, error exchange gone); acking
                // prevents unbounded redelivery of a message with no viable error destination.
            }
        }
        else if (!_errorsDisabled)
        {
            if (_shutdownTimedOut())
            {
                return false;
            }

            await PublishAuditWithDropMetricAsync(publishChannel, args, headers, shutdownToken).ConfigureAwait(false);
        }

        return !_shutdownTimedOut();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
            // Audit queue is a single global destination per the spec — emitting
            // messaging.destination.name here would imply per-queue audit topology
            // that doesn't exist. Tag only system + error.type.
            ServiceConnectMeter.AddAuditDrop(new TagList
            {
                { "messaging.system", "rabbitmq" },
                { "error.type", ExceptionTypeMapper.Map(ex) },
            });
        }
    }

    // Avoid StringBuilder allocation inside DateTime.ToString("O").
    private static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }

    // Test-access surface that exposes the inline header-copy logic so unit tests can
    // assert the eager-decode invariant without driving the full ProcessAsync pipeline.
    // internal for [InternalsVisibleTo].
    internal static Dictionary<string, object> CopyInboundHeadersForTests(BasicDeliverEventArgs args)
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
}
