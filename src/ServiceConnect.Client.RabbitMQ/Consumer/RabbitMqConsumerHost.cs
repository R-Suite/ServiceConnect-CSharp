using System.Linq;
using System.Text;
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
    private readonly RabbitMqAdmissionGate _admissionGate;
    private readonly RabbitMqHeaderValidator _validator;
    private readonly RabbitMqDispatchPipeline _dispatch;
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
    private readonly int _maxHeaderCount;
    private readonly int _maxHeaderValueBytes;

    private readonly RabbitMqChannelHost _channelHost;
    private ConsumerEventHandler? _consumerEventHandler;
    // Per-delivery dispatcher (handler invocation + retry/terminal/audit routing) — built
    // in StartConsumingAsync once the queue / retry-queue names are known. The host owns
    // admission, ack/nack, and lifecycle; the processor is the pure "given a delivery,
    // process and route it" operation.
    private InboundMessageProcessor? _messageProcessor;
    private AsyncEventingBasicConsumer? _consumer;
    // RabbitMQ.Client auto-recovery may re-issue BasicConsumeAsync on reconnect with a
    // different consumer tag. We subscribe to IConnection.ConsumerTagChangeAfterRecoveryAsync
    // to keep _consumerTag current, so a later BasicCancelAsync during DisposeAsync targets
    // the live consumer rather than a stale tag that no longer exists on the broker.
    private string? _consumerTag;
    // Captured at subscribe time so DisposeAsync can `-=` the SAME delegate instance
    // off AsyncEventingBasicConsumer.ReceivedAsync. The other broker-event handlers
    // are method-group references (the unsubscribe binds against the same MethodInfo
    // by identity), but the ReceivedAsync subscription is a lambda — without holding
    // a reference we cannot remove it on dispose, leaking the host reference until
    // the AsyncEventingBasicConsumer itself is collected.
    private AsyncEventHandler<global::RabbitMQ.Client.Events.BasicDeliverEventArgs>? _receivedHandler;
    private bool _autoDelete;
    private string _queueName = "";
    private string _retryQueueName = "";
    private int _shutdownTimedOut;
    // Defends against concurrent DisposeAsync calls. The admission gate's BeginShutdown is
    // idempotent under its own lock, but two concurrent disposes could both pass the
    // IsShuttingDown check before either calls BeginShutdown, then both run teardown.
    // CompareExchange ensures exactly one dispose proceeds; the other returns early.
    private int _disposeStarted;

    // CAS gate for StopAsync. The graceful-stop flow (BasicCancel + drain) is idempotent
    // at each step, but the wrapper guard keeps repeated calls cheap and avoids redundant
    // log warnings on re-cancel attempts.
    private int _stopStarted;
    // Single-use guard: PrepareAsync may run at most once per host instance. The host is
    // documented as single-use (matches Consumer.cs's "fresh host per StartConsumingAsync"
    // pattern). A second Prepare would need to also reset _admissionGate (readonly) and
    // _stopStarted — neither of which currently resets, so the second cycle would silently
    // drop every delivery via the latched admission gate. Single-use makes the constraint
    // explicit and surfaces misuse loudly.
    private int _prepared;
    private readonly CancellationTokenSource _shutdownPublishCts = new();
    // Consumer-lifetime token: created at StartConsumingAsync, cancelled on DisposeAsync.
    // Delivery callbacks hand this to handlers so they observe *consumer* teardown rather
    // than whatever startup CT the caller happened to pass — a startup-scoped token can be
    // cancelled post-startup and would break every later delivery if captured by the callback.
    private readonly CancellationTokenSource _deliveryCts = new();
    // Captured token value: the struct remains usable after _deliveryCts.Dispose(), whereas
    // accessing _deliveryCts.Token would throw ObjectDisposedException. A late delivery
    // fired after DisposeAsync has disposed the CTS must still be able to observe cancellation.
    private CancellationToken _deliveryToken;
    // Captured at subscribe time so DisposeAsync can unsubscribe against the SAME IConnection
    // reference. Re-fetching _connection.UnderlyingConnection at unsubscribe time would return
    // null after the parent Connection's DisposeAsync nulls _connection, leaking these handlers
    // on the original IConnection until GC reclaims it.
    private global::RabbitMQ.Client.IConnection? _subscribedUnderlyingConnection;

    public RabbitMqConsumerHost(
        IServiceConnectConnection connection,
        ITransportConfiguration transportConfiguration,
        IQueueConfiguration queueConfiguration,
        IBusConfiguration busConfiguration,
        MessageRetryHandler retryHandler,
        RabbitMqAdmissionGate admissionGate,
        MessageAuditPublisher auditPublisher,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
        _retryHandler = retryHandler ?? throw new ArgumentNullException(nameof(retryHandler));
        _admissionGate = admissionGate ?? throw new ArgumentNullException(nameof(admissionGate));
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
        _maxHeaderCount = settings.TryGetValue(RabbitMQSettingKeys.MaxHeaderCount, out var maxHeaderCountVal)
            ? Convert.ToInt32(maxHeaderCountVal, System.Globalization.CultureInfo.InvariantCulture)
            : DefaultMaxHeaderCount;
        _maxHeaderValueBytes = settings.TryGetValue(RabbitMQSettingKeys.MaxHeaderValueBytes, out var maxHeaderValueBytesVal)
            ? Convert.ToInt32(maxHeaderValueBytesVal, System.Globalization.CultureInfo.InvariantCulture)
            : DefaultMaxHeaderValueBytes;

        _channelHost = new RabbitMqChannelHost(_connection, _logger, _queueConfiguration.QueueName);

        // Constructed inside the host (not Consumer.cs) because the validator needs
        // GetShutdownPublishToken — a method on the host whose backing CTS lifetime
        // matches the host's single-use cycle. Injecting a delegate keeps the host
        // as the single owner of the CTS without exposing it externally.
        _validator = new RabbitMqHeaderValidator(
            retryHandler,
            _maxInboundMessageSize,
            _maxHeaderCount,
            _maxHeaderValueBytes,
            GetShutdownPublishToken,
            logger);

        // Constructed inside the host for the same reason as the validator: the dispatch
        // pipeline's channel-state guards query host-managed flags (_shutdownTimedOut and
        // _admissionGate.IsShuttingDown). Delegate injection keeps the host as the single
        // owner of those flags while the dispatch class drives the per-delivery ack/nack.
        _dispatch = new RabbitMqDispatchPipeline(
            queueConfiguration.QueueName,
            shutdownTimedOutQuery: () => Volatile.Read(ref _shutdownTimedOut) != 0,
            shutdownStartedQuery: () => _admissionGate.IsShuttingDown,
            logger);
    }

    /// <summary>
    /// Sets up the consumer channel, publish channel, message processor, and broker-event subscriptions.
    /// Does NOT call BasicConsumeAsync — the caller must invoke <see cref="ConsumeMessageTypeAsync"/>
    /// for any required bindings, then <see cref="BeginConsumingAsync"/> to start consuming.
    /// </summary>
    /// <remarks>
    /// Each host instance is single-use: <see cref="PrepareAsync"/> may be called at most once.
    /// Multiple consumers are supported by allocating a fresh <see cref="RabbitMqConsumerHost"/>
    /// per <c>StartConsumingAsync</c> call (the pattern used by <c>Consumer.cs</c>). A second
    /// <see cref="PrepareAsync"/> call throws <see cref="InvalidOperationException"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The host has already been prepared.</exception>
    public async Task PrepareAsync(
        ConsumerEventHandler messageReceived, string queueName,
        bool? autoDelete = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _prepared, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "RabbitMqConsumerHost.PrepareAsync may only be called once per instance. The host is single-use; allocate a fresh instance for each consumer lifecycle.");
        }

        _consumerEventHandler = messageReceived;
        _queueName = queueName;
        _retryQueueName = queueName + RabbitMqQueueNaming.RetryQueueSuffix;

        if (autoDelete.HasValue)
        {
            _autoDelete = autoDelete.Value;
        }

        await _channelHost.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!_disablePrefetch)
        {
            await _channelHost.Model!.BasicQosAsync(0, _prefetchCount, false).ConfigureAwait(false);
        }

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
        _consumer = new AsyncEventingBasicConsumer(_channelHost.Model!);
        _receivedHandler = async (sender, args) => await EventAsync(sender, args, deliveryToken).ConfigureAwait(false);
        _consumer.ReceivedAsync += _receivedHandler;
        // Subscribe broker-initiated shutdown events so a queue deletion, channel close,
        // or connection-level event is observed and logged rather than silently stalling consumption.
        // ShutdownAsync fires on channel shutdown (both client- and server-initiated).
        // UnregisteredAsync fires on broker-initiated basic.cancel (e.g. queue deleted while consuming).
        _consumer.ShutdownAsync += OnConsumerShutdownAsync;
        _consumer.UnregisteredAsync += OnConsumerUnregisteredAsync;
        _subscribedUnderlyingConnection = _connection.UnderlyingConnection;
        if (_subscribedUnderlyingConnection is not null)
        {
            _subscribedUnderlyingConnection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            _subscribedUnderlyingConnection.ConnectionBlockedAsync += OnConnectionBlockedAsync;
            _subscribedUnderlyingConnection.ConnectionUnblockedAsync += OnConnectionUnblockedAsync;
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
        if (_channelHost.Model == null || _consumer == null)
        {
            throw new InvalidOperationException("PrepareAsync must be called before BeginConsumingAsync.");
        }

        // Subscribe to the consumer-tag recovery event BEFORE BasicConsumeAsync. RabbitMQ.Client
        // auto-recovery may fire between the BasicConsumeAsync return and a later subscribe call,
        // changing the broker-assigned tag without us knowing. Subscribing first means tag
        // changes are observed live by the handler. The handler matches on TagBefore == _consumerTag,
        // so the very-first invocation (where _consumerTag is still null) is a safe no-op.
        SubscribeToConsumerTagRecovery();

        // Volatile.Write so a later acquire-fenced read in Stop/Dispose (or in the recovery
        // handler) observes the initial tag without depending on the ambient ordering of the
        // BasicConsumeAsync await's continuation.
        Volatile.Write(ref _consumerTag, await _channelHost.Model!.BasicConsumeAsync(_queueName, false, "", false, false, null, _consumer, cancellationToken).ConfigureAwait(false));
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
        await _channelHost.Model!.QueueBindAsync(_queueName, messageTypeName, string.Empty, null, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True once the broker has issued a basic.cancel against this host's consumer
    /// (queue deleted, policy expired, mirror promoted). Aggregated by <see cref="Consumer"/>
    /// and surfaced through <see cref="IBus.IsConsuming"/> so <c>BusConsumingHealthCheck</c>
    /// flips to Unhealthy without needing its own broker-cancel logic.
    /// </summary>
    internal bool IsCancelledByBroker => _channelHost.IsCancelledByBroker;

    /// <summary>
    /// Drives a delivery directly through the admission and processing pipeline.
    /// Exposed so unit tests can exercise the full EventAsync path without going through
    /// the RabbitMQ broker. Named deliberately so follow-on tests can find it by convention.
    /// </summary>
    internal Task RaiseDeliveryForTests(BasicDeliverEventArgs args, CancellationToken ct = default)
        => EventAsync(this, args, ct);

    // Small orchestrator: admit → validate → dispatch → release. The metric-instrumented
    // handler invocation, ack/nack against the model channel, and ack/nack failure logging
    // all live in RabbitMqDispatchPipeline.
    private async Task EventAsync(object _, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        // Reject before doing any per-delivery work if shutdown has begun: the gate is the
        // single source of truth for admission. A return here is the "drop" path; the
        // broker will redeliver this delivery on the next consumer start.
        if (!_admissionGate.TryAdmit())
        {
            return;
        }

        // Capture channels before any await so that a concurrent DisposeAsync cannot
        // null them out from under the dispatch pipeline. The pipeline's AckOrNackAsync
        // tolerates a null model (logs at Debug, does not ack).
        var model = _channelHost.Model;
        var publishChannel = _channelHost.PublishChannel;

        try
        {
            // CopyInboundHeaders is a pure allocate-and-copy with no side effects, so
            // hoisting the call out of each rule's rejection block is semantically equivalent.
            // The validator routes a rejection through the terminal-failure path; we then
            // ack the broker delivery (processed=true), since redelivery would re-trigger
            // the same rule. The same dict is threaded through to ProcessAsync below so the
            // dispatch hot path does not re-build it (with N redundant UTF-8 decodes of every
            // byte[] header value per delivery).
            var copiedHeaders = CopyInboundHeaders(args);
            var validation = await _validator.ValidateAsync(args, publishChannel!, copiedHeaders, cancellationToken).ConfigureAwait(false);
            if (!validation.Accepted)
            {
                await _dispatch.AckOrNackAsync(model, args, processed: true).ConfigureAwait(false);
                return;
            }

            var messageProcessor = _messageProcessor;
            if (messageProcessor == null)
            {
                _logger.LogWarning("Message processor not initialised — message {DeliveryTag} will be nacked for redelivery", args.DeliveryTag);
                await _dispatch.AckOrNackAsync(model, args, processed: false).ConfigureAwait(false);
                return;
            }

            await _dispatch.DispatchAndAckAsync(messageProcessor, model, publishChannel!, args, copiedHeaders, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Catches admission/header-validation failures that didn't reach the metric-instrumented
            // ProcessAsync scope (e.g. _retryHandler.HandleTerminalFailureAsync throwing). Without
            // this an uncaught exception would propagate into the AMQP consumer event loop. The
            // delivery is nacked-with-requeue so the broker redelivers it once the validator's
            // dependency (typically the publish channel) recovers.
            _logger.LogError(ex, "Error processing message");
            try
            {
                await _dispatch.AckOrNackAsync(model, args, processed: false).ConfigureAwait(false);
            }
            catch (Exception nackEx)
            {
                // AckOrNackAsync swallows its own broker-level errors via LogAckOrNackFailure,
                // so anything that escapes here is a programmer error in the pipeline. Logging
                // it (rather than letting it bubble) keeps the AMQP consumer loop alive.
                _logger.LogError(nackEx, "Error nacking message after validation failure");
            }
        }
        finally
        {
            // Release pairs with the TryAdmit at the top: the gate's invariant is exactly one
            // Release per successful TryAdmit. The pre-finally returns above all happen AFTER
            // admission, so they fall through to this finally.
            _admissionGate.Release();
        }
    }

    private static Dictionary<string, object> CopyInboundHeaders(BasicDeliverEventArgs args)
    {
        var sourceHeaders = args.BasicProperties.Headers;
        // Pre-size +4 to cover the framework stamps that InboundMessageProcessor.ProcessAsync
        // adds downstream (Redelivered, TimeReceived, DestinationMachine, DestinationAddress).
        // Without the headroom the dict rehashes once on the dispatch hot path.
        var headers = new Dictionary<string, object>((sourceHeaders?.Count ?? 0) + 4, StringComparer.Ordinal);
        if (sourceHeaders != null)
        {
            foreach (var kvp in sourceHeaders)
            {
                if (kvp.Value is null)
                {
                    continue;
                }
                // Eagerly decode AMQP byte[] header values to UTF8 strings. The existing
                // HeaderDecoder.Decode string fast-path then short-circuits every downstream
                // decode (dispatcher, processors, telemetry, filters, middleware, handlers),
                // each of which currently re-runs Encoding.UTF8.GetString on the same bytes.
                // Typed values (bool, int, IDictionary, IEnumerable) stay as objects so
                // HeaderDecoder.Render still handles them on demand.
                headers[kvp.Key] = kvp.Value is byte[] bytes
                    ? Encoding.UTF8.GetString(bytes)
                    : kvp.Value;
            }
        }

        return headers;
    }

    // Test-access surface: mirrors the production copy so unit tests can assert the eager-decode
    // invariant without driving the full consumer-host pipeline. internal for [InternalsVisibleTo].
    internal static Dictionary<string, object> CopyInboundHeadersForTests(BasicDeliverEventArgs args)
        => CopyInboundHeaders(args);

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
        // Set the flag *before* logging so a downstream health probe racing with the log call
        // observes Unhealthy on the same tick the operator first sees the warning.
        _channelHost.NotifyBrokerCancelled();
        _logger.LogWarning(
            "AMQP consumer '{ConsumerTag}' unregistered by broker (broker-initiated shutdown) on queue '{Queue}'; reporting unhealthy via BusConsumingHealthCheck",
            _consumerTag, _queueName);
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

    private void SubscribeToConsumerTagRecovery()
    {
        // _subscribedUnderlyingConnection was populated by PrepareAsync; reuse the same captured
        // reference here so the matching unsubscribe in DisposeAsync targets the right instance.
        if (_subscribedUnderlyingConnection is not null)
        {
            _subscribedUnderlyingConnection.ConsumerTagChangeAfterRecoveryAsync += OnConsumerTagChangedAfterRecoveryAsync;
        }
    }

    private Task OnConsumerTagChangedAfterRecoveryAsync(object? sender, ConsumerTagChangedAfterRecoveryEventArgs args)
    {
        // After auto-recovery the broker may assign a new tag for our consumer. Update
        // _consumerTag so the BasicCancelAsync call during DisposeAsync targets the live consumer.
        //
        // Read + write under acquire/release fences so a concurrent Stop/Dispose that reads
        // _consumerTag (line 569 / 644) cannot observe a stale reference: without the fence
        // the JIT may hoist the read across the unsubscribe boundary, sending BasicCancel
        // for a tag the broker has already replaced — orphaning the live consumer until
        // channel teardown lands and the broker happens to flush its dead delivery list.
        var current = Volatile.Read(ref _consumerTag);
        if (string.Equals(args.TagBefore, current, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Consumer tag refreshed after auto-recovery on queue '{Queue}': '{TagBefore}' -> '{TagAfter}'",
                _queueName, args.TagBefore, args.TagAfter);
            Volatile.Write(ref _consumerTag, args.TagAfter);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Issues a graceful stop without tearing down the channel: BasicCancel on the consumer
    /// tag so the broker stops delivering, then drains in-flight handler invocations through
    /// the admission gate. Idempotent — repeated calls return early. Safe to run before
    /// <see cref="DisposeAsync"/>; DisposeAsync repeats the BeginShutdown / BasicCancel /
    /// drain trio (idempotent ops) and then proceeds to close the channel and unsubscribe
    /// handlers.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _stopStarted, 1, 0) != 0)
        {
            return;
        }

        _admissionGate.BeginShutdown();

        // Signal in-flight handlers that shutdown is in progress BEFORE the drain wait.
        // Handlers that respect their CancellationToken (the one threaded through
        // IConsumeContext.CancellationToken from _deliveryToken) can wind down gracefully
        // within the grace window. Without this signal, well-behaved handlers blocked on
        // cancellable I/O have no way to know shutdown is happening and consume the full
        // graceful-shutdown timeout, defeating the grace window's purpose. Cancelling here
        // (vs. after the drain) does not abort handlers that ignore CT — the drain still
        // waits for them.
        try { await _deliveryCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }

        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(_gracefulShutdownTimeoutMs);

        // Unsubscribe the consumer-tag recovery handler BEFORE BasicCancelAsync so a
        // concurrent recovery event cannot swap _consumerTag while BasicCancelAsync is
        // using it. DisposeAsync re-runs this unsubscribe; it's null-safe and idempotent.
        if (_subscribedUnderlyingConnection is not null)
        {
            _subscribedUnderlyingConnection.ConsumerTagChangeAfterRecoveryAsync -= OnConsumerTagChangedAfterRecoveryAsync;
        }

        // Unsubscribe channel-shutdown handlers BEFORE BasicCancelAsync. Sending BasicCancel
        // with a stale tag (replaced by auto-recovery) causes the broker to close the channel
        // with a Library initiator, not Application. Without this unsubscribe the channel host's
        // OnChannelShutdownAsync would flip IsCancelledByBroker, falsely marking the bus
        // Unhealthy even though the stop is intentional. DisposeAsync re-runs the same
        // unsubscribe idempotently via the channel host's disposal path.
        _channelHost.UnsubscribeShutdownHandlers();

        // Snapshot _consumerTag under an acquire fence — pairs with the release-fence
        // write in OnConsumerTagChangedAfterRecoveryAsync. Without the fence the JIT can
        // hoist the read across the unsubscribe boundary above, racing a recovery event
        // that has already swapped the tag.
        var tagForCancel = Volatile.Read(ref _consumerTag);
        if (_channelHost.Model != null && tagForCancel != null)
        {
            try
            {
                if (!await WaitForShutdownOperationAsync(
                        _channelHost.Model!.BasicCancelAsync(tagForCancel, false, cancellationToken),
                        deadline).ConfigureAwait(false))
                {
                    _logger.LogWarning("Timed out cancelling consumer during graceful stop");
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cancelling consumer during graceful stop");
            }
        }

        var drainRemaining = deadline - _timeProvider.GetUtcNow();
        if (drainRemaining > TimeSpan.Zero)
        {
            // Construct the deadline CTS with the host's TimeProvider (matches the existing
            // DisposeAsync drain pattern) and link the caller's cancellation token via
            // Register so either source cancels the drain.
            using var drainCts = new CancellationTokenSource(drainRemaining, _timeProvider);
            using var linkReg = cancellationToken.Register(static cts => ((CancellationTokenSource)cts!).Cancel(), drainCts);
            try
            {
                await _admissionGate.DrainAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Drain timeout — same path DisposeAsync takes; let DisposeAsync's own
                // deadline-driven branch handle the publish-CTS cancellation, since stop
                // alone shouldn't tear down outbound channels.
                Volatile.Write(ref _shutdownTimedOut, 1);
            }
        }
        else
        {
            Volatile.Write(ref _shutdownTimedOut, 1);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Only one DisposeAsync call may proceed; concurrent calls return early. The admission
        // gate's BeginShutdown is idempotent, but two concurrent disposes would otherwise both
        // run the rest of teardown (channel close, CTS dispose), which is not safe to repeat.
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            return;
        }
        _admissionGate.BeginShutdown();

        // Signal in-flight handlers that shutdown is in progress BEFORE the drain wait, so
        // handlers that respect CT can wind down within the grace window. The CTS is still
        // disposed at the end of dispose; this just moves the cancellation signal earlier.
        // Idempotent if StopAsync already ran.
        try { await _deliveryCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }

        var deadline = _timeProvider.GetUtcNow().AddMilliseconds(_gracefulShutdownTimeoutMs);
        var shutdownPublishCts = _shutdownPublishCts;
        _ = CancelHelperPublishesAtDeadlineAsync(shutdownPublishCts, deadline);

        // Unsubscribe the consumer-tag recovery handler BEFORE BasicCancelAsync. A recovery
        // event firing concurrently with the cancel could otherwise swap _consumerTag while
        // BasicCancelAsync is using it, targeting a stale tag. The remaining connection-level
        // unsubscribes (channel/connection shutdown, blocked/unblocked) stay near the end of
        // dispose where they were — those don't read host state during teardown.
        if (_subscribedUnderlyingConnection is not null)
        {
            _subscribedUnderlyingConnection.ConsumerTagChangeAfterRecoveryAsync -= OnConsumerTagChangedAfterRecoveryAsync;
        }

        // Volatile.Read pairs with OnConsumerTagChangedAfterRecoveryAsync's write — same
        // rationale as in StopAsync: defend against a JIT hoist across the unsubscribe.
        var tagForCancel = Volatile.Read(ref _consumerTag);
        if (_channelHost.Model != null && tagForCancel != null)
        {
            try
            {
                if (!await WaitForShutdownOperationAsync(
                        _channelHost.Model!.BasicCancelAsync(tagForCancel, false),
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

        // Event-driven drain: the gate completes its drain task as soon as the last
        // in-flight Release() lands, so the success path is faster than the prior 50ms
        // busy-wait. Cancellation fires when the deadline expires, mapping to the same
        // "set _shutdownTimedOut + cancel helper publishes" behaviour as the old break path.
        var drainRemaining = deadline - _timeProvider.GetUtcNow();
        if (drainRemaining > TimeSpan.Zero)
        {
            using var drainCts = new CancellationTokenSource(drainRemaining, _timeProvider);
            try
            {
                await _admissionGate.DrainAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
            {
                Volatile.Write(ref _shutdownTimedOut, 1);
                await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
            }
        }
        else
        {
            Volatile.Write(ref _shutdownTimedOut, 1);
            await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
        }

        if (_autoDelete && _channelHost.Model != null)
        {
            try
            {
                _logger.LogDebug("Deleting retry queue");
                if (!await WaitForShutdownOperationAsync(
                        _channelHost.Model!.QueueDeleteAsync(_retryQueueName, false, false, false),
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
            // The ReceivedAsync subscription is a lambda captured in _receivedHandler at
            // PrepareAsync time. Without removing it here, a late prefetched delivery
            // arriving between BasicCancelAsync and channel close would fire EventAsync
            // on a disposed host — TryAdmit short-circuits today, but the GC-rooted host
            // reference also leaks until the underlying AsyncEventingBasicConsumer is
            // itself collected. Method-group subscriptions are removable by identity;
            // lambdas require holding the delegate reference.
            if (_receivedHandler is not null)
            {
                _consumer.ReceivedAsync -= _receivedHandler;
                _receivedHandler = null;
            }
        }
        // Unsubscribe against the SAME IConnection reference we subscribed to. Re-fetching
        // _connection.UnderlyingConnection here would return null after the parent Connection's
        // DisposeAsync has already nulled the field, leaking these handlers on the original
        // IConnection until GC reclaims it.
        var subscribedConn = _subscribedUnderlyingConnection;
        if (subscribedConn is not null)
        {
            subscribedConn.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
            subscribedConn.ConnectionBlockedAsync -= OnConnectionBlockedAsync;
            subscribedConn.ConnectionUnblockedAsync -= OnConnectionUnblockedAsync;
            // ConsumerTagChangeAfterRecoveryAsync was already unsubscribed earlier in dispose
            // to prevent recovery-during-cancel swaps; only the connection-level shutdown
            // handlers are unsubscribed here.
            _subscribedUnderlyingConnection = null;
        }

        // Bound the channel-host dispose against the remaining grace window. The channel host
        // passes the token into each CloseAsync call; if the test (or real) time provider fires
        // a deadline, the stalled CloseAsync is abandoned and we continue teardown.
        var closeRemaining = deadline - _timeProvider.GetUtcNow();
        if (closeRemaining > TimeSpan.Zero)
        {
            using var closeCts = new CancellationTokenSource(closeRemaining, _timeProvider);
            var closeTask = _channelHost.DisposeAsync(closeCts.Token).AsTask();
            // Task.Delay with _timeProvider so fake-time providers used in tests can fire
            // the deadline exactly when Advance() crosses the boundary.
            var deadlineTask = Task.Delay(closeRemaining, _timeProvider);
#pragma warning disable VSTHRD003
            if (await Task.WhenAny(closeTask, deadlineTask).ConfigureAwait(false) != closeTask)
            {
                _logger.LogWarning("Timed out closing channels during dispose");
                ObserveAbandonedRpc(closeTask);
            }
#pragma warning restore VSTHRD003
        }
        else
        {
            // Deadline already expired: fire with pre-cancelled token and don't await.
            using var closeCts = new CancellationTokenSource();
            await closeCts.CancelAsync().ConfigureAwait(false);
            ObserveAbandonedRpc(_channelHost.DisposeAsync(closeCts.Token).AsTask());
        }
        await shutdownPublishCts.CancelAsync().ConfigureAwait(false);
        shutdownPublishCts.Dispose();
        var deliveryCts = _deliveryCts;
        try { await deliveryCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        deliveryCts.Dispose();
        // Null the consumer field AFTER channel close so a late delivery firing during the
        // close window (the AsyncEventingBasicConsumer is the source of the ReceivedAsync
        // event and may still emit after we unsubscribed but before the channel close
        // completes) can still be observed by the admission gate, which short-circuits via
        // TryAdmit.
        _consumer = null;
    }

    private async Task CancelHelperPublishesAtDeadlineAsync(CancellationTokenSource shutdownPublishCts, DateTimeOffset deadline)
    {
        try
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
        catch (Exception ex)
        {
            // Fire-and-forget helper: any failure outside the expected OCE / ObjectDisposed
            // paths above must not become an unobserved Task. Warning rather than Debug —
            // a stalled deadline helper means dispose may hang publishes past the grace
            // window without a loud signal in production logs.
            _logger.LogWarning(ex, "CancelHelperPublishesAtDeadlineAsync best-effort recovery faulted");
        }
    }

    private async Task<bool> WaitForShutdownOperationAsync(Task operation, DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            // Even with no remaining budget, attach the observation continuation: the
            // operation may still complete or fault later (e.g. AlreadyClosedException
            // from the subsequent channel close). Same reason as the timeout-wins branch.
            ObserveAbandonedRpc(operation);
            return false;
        }

        var timeoutTask = Task.Delay(remaining, _timeProvider);
#pragma warning disable VSTHRD003 // operation is a Task passed by the caller; this helper bounds its wait against a deadline.
        if (await Task.WhenAny(operation, timeoutTask).ConfigureAwait(false) != operation)
        {
            // The deadline won. The RPC may complete or fault later (e.g. AlreadyClosedException
            // from the subsequent channel close). Attach a benign continuation so the
            // post-deadline fault is observed rather than firing TaskScheduler.UnobservedTaskException.
            ObserveAbandonedRpc(operation);
            return false;
        }

        await operation.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        return true;
    }

    private void ObserveAbandonedRpc(Task operation)
    {
        // Continuation runs only if the operation faults; logs at Debug because an aborted
        // post-deadline RPC is expected during dispose, not an error.
        _ = operation.ContinueWith(
            t =>
            {
                if (t.Exception is { } ex)
                {
                    _logger.LogDebug(ex, "Post-deadline shutdown RPC aborted (expected on channel close)");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
