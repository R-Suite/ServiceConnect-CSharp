using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using System.Linq;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Owns the RabbitMQ channel, consumer, and ack/nack lifecycle for a single queue.
/// Delegates failure-path policy to <see cref="MessageRetryHandler"/> and success-path
/// audit publish to <see cref="MessageAuditPublisher"/>.
/// </summary>
internal sealed class RabbitMqConsumerHost : IAsyncDisposable
{
    private readonly IServiceConnectConnection _connection;
    private readonly IQueueConfiguration _queueConfiguration;
    private readonly MessageRetryHandler _retryHandler;
    private readonly MessageAuditPublisher _auditPublisher;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // Inbound header count and per-value size limits prevent resource exhaustion.
    private const int DefaultMaxHeaderCount = 64;
    private const int DefaultMaxHeaderValueBytes = 8192;

    private readonly bool _errorsDisabled;
    private readonly ushort _prefetchCount;
    private readonly bool _disablePrefetch;
    private readonly IDictionary<string, object?> _queueArguments;
    private readonly int _gracefulShutdownTimeoutMs;
    private readonly bool _includeMachineNameInHeaders;
    private readonly bool _deadLetterUnhandledMessages;
    private readonly long _maxInboundMessageSize;
    private readonly object _callbackAdmissionGate = new();

    private IChannel? _model;
    // RabbitMQ.Client requires per-channel serialization. The consumer channel is used for
    // ack/nack only; all retry/audit/error publishes happen on a dedicated publish channel
    // so helper publishes cannot interleave with the consumer's ack/nack stream.
    private IChannel? _publishChannel;
    private ConsumerEventHandler? _consumerEventHandler;
    private AsyncEventingBasicConsumer? _consumer;
    private string? _consumerTag;
    private bool _autoDelete;
    private string _queueName = "";
    private string _retryQueueName = "";
    private int _messagesBeingProcessed;
    private int _shutdownTimedOut;
    private bool _shutdownStarted;
    private CancellationTokenSource _shutdownPublishCts = new();
    // Consumer-lifetime token: created at StartConsumingAsync, cancelled on DisposeAsync.
    // Delivery callbacks hand this to handlers so they observe *consumer* teardown rather
    // than whatever startup CT the caller happened to pass — a startup-scoped token can be
    // cancelled post-startup and would break every later delivery if captured by the callback.
    private CancellationTokenSource _deliveryCts = new();
    // Captured token value: the struct remains usable after _deliveryCts.Dispose(), whereas
    // accessing _deliveryCts.Token would throw ObjectDisposedException. A late delivery
    // fired after DisposeAsync has disposed the CTS must still be able to observe cancellation.
    private CancellationToken _deliveryToken;

    public RabbitMqConsumerHost(
        IServiceConnectConnection connection,
        ITransportConfiguration transportConfiguration,
        IQueueConfiguration queueConfiguration,
        IBusConfiguration busConfiguration,
        MessageRetryHandler retryHandler,
        MessageAuditPublisher auditPublisher,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _retryHandler = retryHandler ?? throw new ArgumentNullException(nameof(retryHandler));
        _auditPublisher = auditPublisher ?? throw new ArgumentNullException(nameof(auditPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(transportConfiguration);
        ArgumentNullException.ThrowIfNull(busConfiguration);

        // Extract configuration in the constructor and make the fields readonly.
        _includeMachineNameInHeaders = busConfiguration.IncludeMachineNameInHeaders;
        _deadLetterUnhandledMessages = busConfiguration.DeadLetterUnhandledMessages;

        var settings = transportConfiguration.ClientSettings;
        _errorsDisabled = queueConfiguration.DisableErrors;
        _autoDelete = settings.TryGetValue(RabbitMQSettingKeys.AutoDelete, out var autoDeleteVal) && (bool)autoDeleteVal;
        _prefetchCount = settings.TryGetValue(RabbitMQSettingKeys.PrefetchCount, out var prefetchVal)
            ? Convert.ToUInt16(prefetchVal)
            : transportConfiguration.PrefetchCount;
        _disablePrefetch = settings.TryGetValue(RabbitMQSettingKeys.DisablePrefetch, out var disablePrefetchVal) && (bool)disablePrefetchVal;
        _queueArguments = CoerceToQueueArgs(settings, RabbitMQSettingKeys.Arguments);
        _gracefulShutdownTimeoutMs = transportConfiguration.GracefulShutdownTimeoutMilliseconds > 0
            ? transportConfiguration.GracefulShutdownTimeoutMilliseconds
            : 5000;
        _maxInboundMessageSize = settings.TryGetValue(RabbitMQSettingKeys.MessageSize, out var maxSizeVal)
            ? Convert.ToInt64(maxSizeVal)
            : 64 * 1024;
    }

    public async Task StartConsumingAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? exclusive = null, bool? autoDelete = null, CancellationToken cancellationToken = default)
    {
        _consumerEventHandler = messageReceived;
        _queueName = queueName;
        _retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;

        if (autoDelete.HasValue) _autoDelete = autoDelete.Value;

        _model = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);
        // Dedicated publish channel for retry/audit/error; kept separate from the
        // consumer channel because RabbitMQ.Client is not safe to use concurrently on
        // a single channel. Publisher confirms ensure BasicPublishAsync awaits the
        // broker ack before returning, so a lost retry/audit/error publish surfaces as
        // an exception on the consumer path instead of silently disappearing.
        var publishChannelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        _publishChannel = await _connection.CreateChannelAsync(publishChannelOptions, cancellationToken).ConfigureAwait(false);
        if (!_disablePrefetch)
            await _model.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);

        Volatile.Write(ref _shutdownTimedOut, 0);
        // Dispose any CTS replaced here — on first call these are the field-initialised instances,
        // on restart they are the prior cycle's CTSes. Leaving them undisposed leaks one per
        // start-stop-start cycle.
        _shutdownPublishCts.Dispose();
        _shutdownPublishCts = new CancellationTokenSource();
        _deliveryCts.Dispose();
        _deliveryCts = new CancellationTokenSource();
        _deliveryToken = _deliveryCts.Token;
        // The lambda captures the delivery *token* (not _deliveryCts.Token accessor) so a
        // late delivery fired after DisposeAsync disposed the CTS can still run without
        // throwing ObjectDisposedException from the Token property.
        var deliveryToken = _deliveryToken;
        _consumer = new AsyncEventingBasicConsumer(_model);
        _consumer.ReceivedAsync += async (sender, args) => await EventAsync(sender, args, deliveryToken).ConfigureAwait(false);
        // M13: subscribe broker-initiated shutdown events so a queue deletion, channel close,
        // or connection-level event is observed and logged rather than silently stalling consumption.
        // ShutdownAsync fires on channel shutdown (both client- and server-initiated).
        // UnregisteredAsync fires on broker-initiated basic.cancel (e.g. queue deleted while consuming).
        _consumer.ShutdownAsync += OnConsumerShutdownAsync;
        _consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
        _model.ChannelShutdownAsync += OnChannelShutdownAsync;
        var underlying = _connection.UnderlyingConnection;
        if (underlying is not null)
        {
            underlying.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            underlying.ConnectionBlockedAsync += OnConnectionBlockedAsync;
            underlying.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;
        }

        _consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, _consumerTag);
    }

    public async Task ConsumeMessageTypeAsync(string messageTypeName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Pass cancellationToken as a method parameter instead of storing it.
    private async Task EventAsync(object consumer, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        // Capture channels before any await so that a concurrent DisposeAsync cannot
        // null them out from under us in the finally block.
        var model = _model;
        var publishChannel = _publishChannel;
        bool processed = false;
        bool callbackAdmitted = false;
        try
        {
            lock (_callbackAdmissionGate)
            {
                if (_shutdownStarted)
                    return;

                _messagesBeingProcessed++;
                callbackAdmitted = true;
            }

            // M14: ContainsKey admits a key whose value is null; use TryGetValue+non-null instead.
            // A null-valued TypeName passes ContainsKey but CopyInboundHeaders skips null values,
            // so the dispatch-site indexer would throw KeyNotFoundException and burn a retry cycle
            // on a guaranteed-fail dispatch. Reject at admission instead.
            static bool HasNonNullValue(IDictionary<string, object?> h, string key)
                => h.TryGetValue(key, out var v) && v is not null;

            if (args.BasicProperties.Headers == null ||
                (!HasNonNullValue(args.BasicProperties.Headers, HeaderKeys.TypeName) &&
                 !HasNonNullValue(args.BasicProperties.Headers, HeaderKeys.FullTypeName)))
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel!,
                    args,
                    CopyInboundHeaders(args),
                    new InvalidOperationException("Message headers must contain type name."),
                    GetShutdownPublishToken()).ConfigureAwait(false);
                processed = true;
                return;
            }

            if (args.Body.Length > _maxInboundMessageSize)
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel!,
                    args,
                    CopyInboundHeaders(args),
                    new InvalidOperationException(
                        $"Inbound message size {args.Body.Length} bytes exceeds configured limit {_maxInboundMessageSize} bytes."),
                    GetShutdownPublishToken())
                    .ConfigureAwait(false);
                processed = true;
                return;
            }

            var inboundHeaders = args.BasicProperties.Headers;
            if (inboundHeaders != null && inboundHeaders.Count > DefaultMaxHeaderCount)
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel!,
                    args,
                    CopyInboundHeaders(args),
                    new InvalidOperationException(
                        $"Inbound header count {inboundHeaders.Count} exceeds configured limit {DefaultMaxHeaderCount}."),
                    GetShutdownPublishToken())
                    .ConfigureAwait(false);
                processed = true;
                return;
            }

            if (inboundHeaders != null)
            {
                foreach (var kvp in inboundHeaders)
                {
                    if (kvp.Value is byte[] bytes && bytes.Length > DefaultMaxHeaderValueBytes)
                    {
                        await _retryHandler.HandleTerminalFailureAsync(
                            publishChannel!,
                            args,
                            CopyInboundHeaders(args),
                            new InvalidOperationException(
                                $"Inbound header '{kvp.Key}' size {bytes.Length} bytes exceeds configured limit {DefaultMaxHeaderValueBytes} bytes."),
                            GetShutdownPublishToken())
                            .ConfigureAwait(false);
                        processed = true;
                        return;
                    }
                }
            }

            processed = await ProcessMessageAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
        }
        finally
        {
            try
            {
                if (callbackAdmitted)
                {
                    if (model == null)
                    {
                        _logger.LogWarning("Channel was null during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
                    }
                    else if (Volatile.Read(ref _shutdownTimedOut) != 0)
                    {
                        _logger.LogDebug("Shutdown grace window expired before finishing message {DeliveryTag}; leaving unacked for broker redelivery", args.DeliveryTag);
                    }
                    else if (processed)
                        await model.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
                    else
                        await model.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
                }
            }
            catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException ex)
            {
                // Expected when the connection/channel is torn down concurrently with
                // message processing (typical during shutdown). The broker will redeliver
                // unacked messages after the connection drops, so this is not an error.
                if (_shutdownStarted)
                    _logger.LogDebug(ex, "Channel already closed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
                else
                    _logger.LogWarning(ex, "Channel already closed while acking/nacking message {DeliveryTag}", args.DeliveryTag);
            }
            catch (ObjectDisposedException ex)
            {
                if (_shutdownStarted)
                    _logger.LogDebug(ex, "Channel disposed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
                else
                    _logger.LogWarning(ex, "Channel disposed while acking/nacking message {DeliveryTag}", args.DeliveryTag);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error acking/nacking the message");
            }
            finally
            {
                if (callbackAdmitted)
                    Interlocked.Decrement(ref _messagesBeingProcessed);
            }
        }
    }

    private static Dictionary<string, object> CopyInboundHeaders(BasicDeliverEventArgs args)
    {
        var sourceHeaders = args.BasicProperties.Headers;
        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 0) + 1);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is not null)
                    headers[kvp.Key] = kvp.Value;
            }
        }

        return headers;
    }

    private async Task<bool> ProcessMessageAsync(IChannel publishChannel, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        ConsumeEventResult result;
        // Pre-size the dict to the incoming header count plus 3 consumer-added
        // headers (TimeReceived, DestinationMachine, DestinationAddress) so we
        // avoid rehashes during the copy on this per-message hot path.
        var sourceHeaders = args.BasicProperties.Headers;

        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 4) + 3);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is not null) headers[kvp.Key] = kvp.Value;
            }
        }

        if (args.Redelivered)
            HeaderHelpers.SetHeader(headers, HeaderKeys.Redelivered, true);

        try
        {
            HeaderHelpers.SetHeader(headers, HeaderKeys.TimeReceived, FormatTimestamp(_timeProvider.GetUtcNow().UtcDateTime));
            if (_includeMachineNameInHeaders)
                HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationMachine, Environment.MachineName);
            HeaderHelpers.SetHeader(headers, HeaderKeys.DestinationAddress, _queueConfiguration.QueueName);

            // Prefer FullTypeName; fall back to TypeName. Use TryGetValue to avoid KeyNotFoundException.
            // Admission already guarantees at least one is present with a non-null value, but
            // FullTypeName could be null-valued while TypeName is valid — check the value.
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw) || typeNameRaw is null)
                headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
            string typeName = HeaderDecoder.Decode(typeNameRaw) ?? "";

            if (_consumerEventHandler == null)
            {
                _logger.LogError("Consumer event handler not set — message will be nacked for redelivery. Queue: {Queue}", _queueConfiguration.QueueName);
                result = new ConsumeEventResult { Success = false };
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

        if (!result.Success)
        {
            if (Volatile.Read(ref _shutdownTimedOut) != 0)
                return false;

            try
            {
                await _retryHandler.HandleFailureAsync(
                    publishChannel,
                    _retryQueueName,
                    args,
                    headers,
                    result.Exception,
                    GetShutdownPublishToken()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownPublishCts.IsCancellationRequested)
            {
                // Shutdown grace window expired mid-publish — propagate so the outer finally
                // leaves the message unacked for broker redelivery after reconnection.
                throw;
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx,
                    "Retry publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
                // Intentionally swallow: the message is already failed and we cannot retry-publish it.
                // Acking now (processed = true, returned below) prevents the broker from redelivering it
                // into the same failed path. Without this, the exception propagates to the outer catch in
                // EventAsync, sets processed=false, and the finally block nacks with requeue:true →
                // hot-loop (measured at 73,462 invocations/10s in Phase 0 Task 4).
            }
        }
        else if (result.NotHandled && _deadLetterUnhandledMessages && !_errorsDisabled)
        {
            if (Volatile.Read(ref _shutdownTimedOut) != 0)
                return false;

            // Route via the terminal-failure path (error exchange) — a message with no
            // handler is not a retryable condition, so bypass the retry queue.
            if (!headers.TryGetValue(HeaderKeys.FullTypeName, out var typeNameRaw))
                headers.TryGetValue(HeaderKeys.TypeName, out typeNameRaw);
            var typeName = HeaderDecoder.Decode(typeNameRaw) ?? "<unknown>";

            try
            {
                await _retryHandler.HandleTerminalFailureAsync(
                    publishChannel,
                    args,
                    headers,
                    new InvalidOperationException($"No processor handled message of type '{typeName}'."),
                    GetShutdownPublishToken()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdownPublishCts.IsCancellationRequested)
            {
                // Shutdown grace window expired mid-publish — propagate so the outer finally
                // leaves the message unacked for broker redelivery after reconnection.
                throw;
            }
            catch (Exception terminalEx)
            {
                _logger.LogError(terminalEx,
                    "Terminal-failure publish failed for MessageId {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}; dropping to prevent unbounded redelivery loop.",
                    args.BasicProperties.MessageId, args.DeliveryTag, _queueConfiguration.QueueName);
                // Intentionally swallow — same rationale as the HandleFailureAsync catch above.
            }
        }
        else if (!_errorsDisabled)
        {
            if (Volatile.Read(ref _shutdownTimedOut) != 0)
                return false;

            // Audit publish failures must not fail message delivery — audit is an
            // observability side-effect, not part of the business transaction. A
            // throw here would bubble out of ProcessMessageAsync, leave `processed`
            // false in the caller (EventAsync), and the already-handled message would
            // be nacked with requeue:true → duplicate handler invocation.
            try
            {
                await _auditPublisher.PublishAuditIfEnabledAsync(publishChannel, args, headers, GetShutdownPublishToken()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish audit message for delivery {DeliveryTag}; continuing to ack the original message", args.DeliveryTag);
            }
        }

        return Volatile.Read(ref _shutdownTimedOut) == 0;
    }

    private CancellationToken GetShutdownPublishToken()
    {
        return _shutdownPublishCts.Token;
    }

    // M13: broker-initiated shutdown event handlers.

    private Task OnConsumerShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        _logger.LogWarning(
            "AMQP consumer '{ConsumerTag}' shutdown: {ReplyCode} {ReplyText}",
            _consumerTag, args.ReplyCode, args.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnConsumerUnregisteredAsync(object? sender, ConsumerEventArgs args)
    {
        // Fires on broker-initiated basic.cancel (e.g. queue deleted while consuming).
        _logger.LogWarning(
            "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'",
            _consumerTag, _queueName);
        return Task.CompletedTask;
    }

    private Task OnChannelShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        _logger.LogWarning(
            "AMQP channel shutdown for queue '{Queue}': {ReplyCode} {ReplyText}",
            _queueName, args.ReplyCode, args.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnConnectionShutdownAsync(object? sender, ShutdownEventArgs args)
    {
        _logger.LogWarning(
            "AMQP connection shutdown for queue '{Queue}': {ReplyCode} {ReplyText}",
            _queueName, args.ReplyCode, args.ReplyText);
        return Task.CompletedTask;
    }

    private Task OnConnectionBlockedAsync(object? sender, ConnectionBlockedEventArgs args)
    {
        _logger.LogWarning(
            "AMQP connection blocked for queue '{Queue}': {Reason}",
            _queueName, args.Reason);
        return Task.CompletedTask;
    }

    private Task OnConnectionUnblockedAsync(object? sender, AsyncEventArgs args)
    {
        _logger.LogInformation(
            "AMQP connection unblocked for queue '{Queue}'",
            _queueName);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_callbackAdmissionGate)
        {
            if (_shutdownStarted)
                return;

            _shutdownStarted = true;
        }

        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(_gracefulShutdownTimeoutMs);
        var shutdownPublishCts = _shutdownPublishCts;
        _ = CancelHelperPublishesAtDeadlineAsync(shutdownPublishCts, deadline);

        if (_model != null && _consumerTag != null)
        {
            try
            {
                if (!await WaitForShutdownOperationAsync(
                        _model.BasicCancelAsync(_consumerTag, false),
                        deadline).ConfigureAwait(false))
                {
                    _logger.LogWarning("Timed out cancelling consumer during dispose");
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cancelling consumer during dispose");
            }
        }

        while (Volatile.Read(ref _messagesBeingProcessed) > 0)
        {
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                Volatile.Write(ref _shutdownTimedOut, 1);
                shutdownPublishCts.Cancel();
                break;
            }

            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50),
                _timeProvider).ConfigureAwait(false);
        }

        if (_autoDelete && _model != null)
        {
            try
            {
                _logger.LogDebug("Deleting retry queue");
                if (!await WaitForShutdownOperationAsync(
                        _model.QueueDeleteAsync(_retryQueueName, false, false, false),
                        deadline).ConfigureAwait(false))
                {
                    _logger.LogWarning("Timed out deleting retry queue during dispose");
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error deleting retry queue");
            }
        }

        // M13: unsubscribe broker-initiated shutdown handlers to prevent leaks on restart.
        if (_consumer is not null)
        {
            _consumer.ShutdownAsync -= OnConsumerShutdownAsync;
            _consumer.UnregisteredAsync -= OnConsumerUnregisteredAsync;
        }
        if (_model is not null)
            _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
        var underlyingConn = _connection.UnderlyingConnection;
        if (underlyingConn is not null)
        {
            underlyingConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
            underlyingConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
            underlyingConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
        }

        await CloseChannelAsync(deadline).ConfigureAwait(false);
        await ClosePublishChannelAsync(deadline).ConfigureAwait(false);
        shutdownPublishCts.Cancel();
        shutdownPublishCts.Dispose();
        var deliveryCts = _deliveryCts;
        try { deliveryCts.Cancel(); } catch (ObjectDisposedException) { }
        deliveryCts.Dispose();
    }

    private async Task CancelHelperPublishesAtDeadlineAsync(CancellationTokenSource shutdownPublishCts, DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            shutdownPublishCts.Cancel();
            return;
        }

        try
        {
            await Task.Delay(remaining, _timeProvider, shutdownPublishCts.Token).ConfigureAwait(false);
            shutdownPublishCts.Cancel();
        }
        catch (OperationCanceledException) when (shutdownPublishCts.IsCancellationRequested)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    private async Task<bool> WaitForShutdownOperationAsync(Task operation, DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return false;

        var timeoutTask = Task.Delay(remaining, _timeProvider);
        if (await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false) != operation)
            return false;

        await operation.ConfigureAwait(false);
        return true;
    }

    // Avoid StringBuilder allocation inside DateTime.ToString("O").
    private static string FormatTimestamp(DateTime dt)
    {
        Span<char> buffer = stackalloc char[33]; // "O" format max length
        dt.TryFormat(buffer, out int charsWritten, "O");
        return new string(buffer[..charsWritten]);
    }

    private async Task CloseChannelAsync(DateTimeOffset deadline)
    {
        if (_model == null) return;
        try
        {
            if (_model.IsOpen)
            {
                if (!await WaitForShutdownOperationAsync(_model.CloseAsync(200, "Goodbye", false), deadline).ConfigureAwait(false))
                    _logger.LogWarning("Timed out closing channel during dispose");
            }

            _model.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing channel during dispose");
        }
        _model = null;
        _consumerTag = null;
    }

    private async Task ClosePublishChannelAsync(DateTimeOffset deadline)
    {
        var publishChannel = _publishChannel;
        if (publishChannel == null) return;
        try
        {
            if (publishChannel.IsOpen)
            {
                if (!await WaitForShutdownOperationAsync(publishChannel.CloseAsync(200, "Goodbye", false), deadline).ConfigureAwait(false))
                    _logger.LogWarning("Timed out closing publish channel during dispose");
            }

            publishChannel.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error closing publish channel during dispose");
        }
        _publishChannel = null;
    }

    private static Dictionary<string, object?> CoerceToQueueArgs(IReadOnlyDictionary<string, object> settings, string key)
    {
        // Accept any dictionary-shaped value; copy into a plain Dictionary<,> so downstream
        // mutation and enumeration operate on a concrete, non-read-only instance. Direct casting
        // to IDictionary<,> broke for callers using custom IReadOnlyDictionary implementations
        // that do not also implement IDictionary.
        if (!settings.TryGetValue(key, out var raw) || raw is null)
            return new Dictionary<string, object?>();
        return raw switch
        {
            Dictionary<string, object?> d => d,
            IDictionary<string, object?> id => new Dictionary<string, object?>(id),
            IReadOnlyDictionary<string, object?> rd => rd.ToDictionary(kv => kv.Key, kv => kv.Value),
            _ => throw new InvalidOperationException(
                $"Setting '{key}' must be IDictionary<string, object?> or IReadOnlyDictionary<string, object?>; got {raw.GetType().FullName}."),
        };
    }
}
