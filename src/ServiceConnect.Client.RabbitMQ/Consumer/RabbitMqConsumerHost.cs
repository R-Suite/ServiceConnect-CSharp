using System.Linq;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

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
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _callbackAdmissionGate = new();
#else
    private readonly object _callbackAdmissionGate = new();
#endif

    private IChannel? _model;
    // RabbitMQ.Client requires per-channel serialization. The consumer channel is used for
    // ack/nack only; all retry/audit/error publishes happen on a dedicated publish channel
    // so helper publishes cannot interleave with the consumer's ack/nack stream.
    private IChannel? _publishChannel;
    private ConsumerEventHandler? _consumerEventHandler;
    // Per-delivery dispatcher (handler invocation + retry/terminal/audit routing) — built
    // in StartConsumingAsync once the queue / retry-queue names are known. The host owns
    // admission, ack/nack, and lifecycle; the processor is the pure "given a delivery,
    // process and route it" operation.
    private InboundMessageProcessor? _messageProcessor;
    private AsyncEventingBasicConsumer? _consumer;
    private string? _consumerTag;
    private bool _autoDelete;
    private string _queueName = "";
    private string _retryQueueName = "";
    // All reads/writes use atomic primitives (Interlocked.Increment, Interlocked.Decrement,
    // Volatile.Read). The pre-fix bug was a non-atomic `_messagesBeingProcessed++` (read-modify-write)
    // inside the admission lock racing with a lock-free Interlocked.Decrement: a concurrent
    // decrement between the increment's read and write would be silently overwritten. The
    // increment now uses Interlocked.Increment, but is kept INSIDE the admission lock so
    // DisposeAsync (which sets _shutdownStarted under the same lock) cannot release the drain
    // wait before an admitted delivery has been counted.
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
            ? Convert.ToUInt16(prefetchVal, System.Globalization.CultureInfo.InvariantCulture)
            : transportConfiguration.PrefetchCount;
        _disablePrefetch = settings.TryGetValue(RabbitMQSettingKeys.DisablePrefetch, out var disablePrefetchVal) && (bool)disablePrefetchVal;
        _queueArguments = CoerceToQueueArgs(settings, RabbitMQSettingKeys.Arguments);
        _gracefulShutdownTimeoutMs = transportConfiguration.GracefulShutdownTimeoutMilliseconds > 0
            ? transportConfiguration.GracefulShutdownTimeoutMilliseconds
            : 5000;
        _maxInboundMessageSize = settings.TryGetValue(RabbitMQSettingKeys.MessageSize, out var maxSizeVal)
            ? Convert.ToInt64(maxSizeVal, System.Globalization.CultureInfo.InvariantCulture)
            : 64 * 1024;
    }

    /// <summary>
    /// Sets up the consumer channel, publish channel, message processor, and broker-event subscriptions.
    /// Does NOT call BasicConsumeAsync — the caller must invoke <see cref="ConsumeMessageTypeAsync"/>
    /// for any required bindings, then <see cref="BeginConsumingAsync"/> to start consuming.
    /// </summary>
    public async Task PrepareAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? autoDelete = null, CancellationToken cancellationToken = default)
    {
        _consumerEventHandler = messageReceived;
        _queueName = queueName;
        _retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;

        if (autoDelete.HasValue)
        {
            _autoDelete = autoDelete.Value;
        }

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
        {
            await _model.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);
        }

        Volatile.Write(ref _shutdownTimedOut, 0);
        // Dispose any CTS replaced here — on first call these are the field-initialised instances,
        // on restart they are the prior cycle's CTSes. Leaving them undisposed leaks one per
        // start-stop-start cycle.
        _shutdownPublishCts.Dispose();
        _shutdownPublishCts = new CancellationTokenSource();
        _deliveryCts.Dispose();
        _deliveryCts = new CancellationTokenSource();
        _deliveryToken = _deliveryCts.Token;
        _messageProcessor = new InboundMessageProcessor(
            _consumerEventHandler,
            _retryHandler,
            _auditPublisher,
            _queueConfiguration,
            _timeProvider,
            _logger,
            _retryQueueName,
            _errorsDisabled,
            _deadLetterUnhandledMessages,
            _includeMachineNameInHeaders,
            shutdownTimedOut: () => Volatile.Read(ref _shutdownTimedOut) != 0,
            shutdownPublishToken: () => _shutdownPublishCts.Token);
        // The lambda captures the delivery *token* (not _deliveryCts.Token accessor) so a
        // late delivery fired after DisposeAsync disposed the CTS can still run without
        // throwing ObjectDisposedException from the Token property.
        var deliveryToken = _deliveryToken;
        _consumer = new AsyncEventingBasicConsumer(_model);
        _consumer.ReceivedAsync += async (sender, args) => await EventAsync(sender, args, deliveryToken).ConfigureAwait(false);
        // Subscribe broker-initiated shutdown events so a queue deletion, channel close,
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
    }

    /// <summary>
    /// Issues the BasicConsumeAsync that puts this host into the actively-consuming state.
    /// MUST be called AFTER all <see cref="ConsumeMessageTypeAsync"/> bindings are complete —
    /// running QueueBindAsync on the consumer channel after BasicConsume violates RabbitMQ.Client's
    /// per-channel serialisation contract.
    /// </summary>
    public async Task BeginConsumingAsync(CancellationToken cancellationToken = default)
    {
        if (_model == null || _consumer == null)
        {
            throw new InvalidOperationException("PrepareAsync must be called before BeginConsumingAsync.");
        }

        _consumerTag = await _model.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Started consuming on {QueueName}, tag={ConsumerTag}", _queueName, _consumerTag);
    }

    /// <summary>
    /// Backward-compatible shorthand: <see cref="PrepareAsync"/> followed by <see cref="BeginConsumingAsync"/>.
    /// Bind any per-message-type queues via <see cref="ConsumeMessageTypeAsync"/> BETWEEN these two calls
    /// to honour RabbitMQ.Client's per-channel serialisation contract.
    /// </summary>
    public async Task StartConsumingAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? autoDelete = null, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(messageReceived, queueName, autoDelete, cancellationToken).ConfigureAwait(false);
        await BeginConsumingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ConsumeMessageTypeAsync(string messageTypeName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drives a delivery directly through the admission and processing pipeline.
    /// Exposed so unit tests can exercise the full EventAsync path without going through
    /// the RabbitMQ broker. Named deliberately so follow-on tests can find it by convention.
    /// </summary>
    internal Task RaiseDeliveryForTests(BasicDeliverEventArgs args, CancellationToken ct = default)
        => EventAsync(this, args, ct);

    // Pass cancellationToken as a method parameter instead of storing it.
    private async Task EventAsync(object _, BasicDeliverEventArgs args, CancellationToken cancellationToken)
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
                {
                    return;
                }

                // Increment INSIDE the lock so DisposeAsync, which sets _shutdownStarted under the
                // same lock, cannot release the drain wait before this admitted delivery has been
                // counted. Interlocked.Increment is atomic — calling it under the lock matches the
                // atomic discipline of the matching Interlocked.Decrement at the bottom of the
                // EventAsync finally block.
                Interlocked.Increment(ref _messagesBeingProcessed);
                callbackAdmitted = true;
            }

            // ContainsKey admits a key whose value is null; use TryGetValue+non-null instead.
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
                        byteSize = System.Text.Encoding.UTF8.GetByteCount(s);
                    }
                    else
                    {
                        // Non-string, non-byte-array headers (int, bool, etc.) are size-bounded by their type.
                        continue;
                    }

                    if (byteSize > DefaultMaxHeaderValueBytes)
                    {
                        await _retryHandler.HandleTerminalFailureAsync(
                            publishChannel!,
                            args,
                            CopyInboundHeaders(args),
                            new InvalidOperationException(
                                $"Inbound header '{kvp.Key}' size {byteSize} bytes exceeds configured limit {DefaultMaxHeaderValueBytes} bytes."),
                            GetShutdownPublishToken())
                            .ConfigureAwait(false);
                        processed = true;
                        return;
                    }
                }
            }

            var messageProcessor = _messageProcessor;
            if (messageProcessor == null)
            {
                _logger.LogWarning("Message processor not initialised — message {DeliveryTag} will be nacked for redelivery", args.DeliveryTag);
                return;  // processed stays false; finally nacks-with-requeue
            }

            processed = await messageProcessor.ProcessAsync(publishChannel!, args, cancellationToken).ConfigureAwait(false);
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
                        // Channel was nulled by concurrent DisposeAsync. Expected during teardown;
                        // broker will redeliver unacked messages on next consumer start.
                        _logger.LogDebug("Channel was null during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
                    }
                    else if (!model.IsOpen)
                    {
                        // Channel closed concurrently. Expected during teardown / connection drop.
                        _logger.LogDebug("Channel was closed during ack/nack — message {DeliveryTag} may be redelivered", args.DeliveryTag);
                    }
                    else if (Volatile.Read(ref _shutdownTimedOut) != 0)
                    {
                        _logger.LogDebug("Shutdown grace window expired before finishing message {DeliveryTag}; leaving unacked for broker redelivery", args.DeliveryTag);
                    }
                    else if (processed)
                    {
                        await model.BasicAckAsync(args.DeliveryTag, false).ConfigureAwait(false);
                    }
                    else
                    {
                        await model.BasicNackAsync(args.DeliveryTag, false, true).ConfigureAwait(false);
                    }
                }
            }
            catch (global::RabbitMQ.Client.Exceptions.AlreadyClosedException ex)
            {
                // Expected when the connection/channel is torn down concurrently with
                // message processing (typical during shutdown). The broker will redeliver
                // unacked messages after the connection drops, so this is not an error.
                if (_shutdownStarted)
                {
                    _logger.LogDebug(ex, "Channel already closed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
                }
                else
                {
                    _logger.LogWarning(ex, "Channel already closed while acking/nacking message {DeliveryTag}", args.DeliveryTag);
                }
            }
            catch (ObjectDisposedException ex)
            {
                if (_shutdownStarted)
                {
                    _logger.LogDebug(ex, "Channel disposed while acking/nacking message {DeliveryTag} during shutdown", args.DeliveryTag);
                }
                else
                {
                    _logger.LogWarning(ex, "Channel disposed while acking/nacking message {DeliveryTag}", args.DeliveryTag);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error acking/nacking the message");
            }
            finally
            {
                if (callbackAdmitted)
                {
                    Interlocked.Decrement(ref _messagesBeingProcessed);
                }
            }
        }
    }

    private static Dictionary<string, object> CopyInboundHeaders(BasicDeliverEventArgs args)
    {
        var sourceHeaders = args.BasicProperties.Headers;
        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 0) + 1, StringComparer.Ordinal);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is not null)
                {
                    headers[kvp.Key] = kvp.Value;
                }
            }
        }

        return headers;
    }

    private CancellationToken GetShutdownPublishToken()
    {
        return _shutdownPublishCts.Token;
    }

    // Broker-initiated shutdown event handlers.

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
            {
                return;
            }

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
                await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
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

        // Unsubscribe broker-initiated shutdown handlers to prevent leaks on restart.
        if (_consumer is not null)
        {
            _consumer.ShutdownAsync -= OnConsumerShutdownAsync;
            _consumer.UnregisteredAsync -= OnConsumerUnregisteredAsync;
        }
        if (_model is not null)
        {
            _model.ChannelShutdownAsync -= OnChannelShutdownAsync;
        }

        var underlyingConn = _connection.UnderlyingConnection;
        if (underlyingConn is not null)
        {
            underlyingConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
            underlyingConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
            underlyingConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
        }

        await CloseChannelAsync(deadline).ConfigureAwait(false);
        await ClosePublishChannelAsync(deadline).ConfigureAwait(false);
        await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
        shutdownPublishCts.Dispose();
        var deliveryCts = _deliveryCts;
        try { await deliveryCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        deliveryCts.Dispose();
    }

    private async Task CancelHelperPublishesAtDeadlineAsync(CancellationTokenSource shutdownPublishCts, DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await Task.Delay(remaining, _timeProvider, shutdownPublishCts.Token).ConfigureAwait(false);
            await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
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
        {
            return false;
        }

        var timeoutTask = Task.Delay(remaining, _timeProvider);
#pragma warning disable VSTHRD003 // operation is a Task passed by the caller; this helper bounds its wait against a deadline.
        if (await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false) != operation)
        {
            return false;
        }

        await operation.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        return true;
    }

    private async Task CloseChannelAsync(DateTimeOffset deadline)
    {
        if (_model == null)
        {
            return;
        }

        try
        {
            if (_model.IsOpen)
            {
                if (!await WaitForShutdownOperationAsync(_model.CloseAsync(200, "Goodbye", false), deadline).ConfigureAwait(false))
                {
                    _logger.LogWarning("Timed out closing channel during dispose");
                }
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
        if (publishChannel == null)
        {
            return;
        }

        try
        {
            if (publishChannel.IsOpen)
            {
                if (!await WaitForShutdownOperationAsync(publishChannel.CloseAsync(200, "Goodbye", false), deadline).ConfigureAwait(false))
                {
                    _logger.LogWarning("Timed out closing publish channel during dispose");
                }
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
        {
            return [];
        }

        return raw switch
        {
            Dictionary<string, object?> d => d,
            IDictionary<string, object?> id => new Dictionary<string, object?>(id, StringComparer.Ordinal),
            IReadOnlyDictionary<string, object?> rd => rd.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            _ => throw new InvalidOperationException(
                $"Setting '{key}' must be IDictionary<string, object?> or IReadOnlyDictionary<string, object?>; got {raw.GetType().FullName}."),
        };
    }
}
