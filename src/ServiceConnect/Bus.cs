using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;

namespace ServiceConnect;

/// <summary>
/// Default <see cref="IBus"/> implementation that coordinates serialization, filtering,
/// transport dispatch, request-reply tracking, and message consumption.
/// </summary>
public sealed class Bus : IBus
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly ISendMessagePipeline _sendPipeline;
    private readonly IRequestReplyManager _requestReplyManager;
    private readonly ILogger<Bus> _logger;
    private readonly IQueueConfiguration _queueConfig;
    private readonly IMessageDispatcher _dispatcher;
    private readonly IList<HandlerReference> _handlerReferences;
    private readonly IConsumer? _consumer;
    private readonly IProducer? _producer;
    private readonly ITimeoutStore? _timeoutStore;
    private readonly ConsumeContextAccessor _consumeContextAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConsumeScopeAccessor _scopeAccessor;
    private readonly bool _hasOutgoingFilters;
    private readonly TimeSpan _disposeTimeout;
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _stateLock = new();
#else
    private readonly object _stateLock = new();
#endif
    private readonly SemaphoreSlim _lifecycleSemaphore = new(1, 1);
    private volatile bool _consuming;
    private bool _stopped;
    // 0 = alive, 1 = disposed. Accessed via Interlocked/Volatile only — never under _stateLock —
    // so DisposeAsync can publish disposal atomically without ordering it against the lifecycle semaphore.
    private int _disposed;

    internal Bus(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        ISendMessagePipeline sendPipeline,
        IRequestReplyManager requestReplyManager,
        ILogger<Bus> logger,
        IQueueConfiguration queueConfig,
        IMessageDispatcher dispatcher,
        IList<HandlerReference> handlerReferences,
        IPipelineConfiguration pipelineConfig,
        IServiceScopeFactory scopeFactory,
        ConsumeScopeAccessor scopeAccessor,
        IConsumer? consumer = null,
        IProducer? producer = null,
        TimeSpan? disposeTimeout = null,
        ITimeoutStore? timeoutStore = null,
        ConsumeContextAccessor? consumeContextAccessor = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
        _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueConfig = queueConfig ?? throw new ArgumentNullException(nameof(queueConfig));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handlerReferences = handlerReferences ?? throw new ArgumentNullException(nameof(handlerReferences));
        if (pipelineConfig == null)
        {
            throw new ArgumentNullException(nameof(pipelineConfig));
        }

        _hasOutgoingFilters = pipelineConfig.OutgoingFilters.Count > 0;
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
        _consumer = consumer;
        _producer = producer;
        _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(30);
        _timeoutStore = timeoutStore;
        _consumeContextAccessor = consumeContextAccessor ?? new ConsumeContextAccessor();
    }

    /// <inheritdoc />
    public bool IsConsuming => _consuming;

    /// <inheritdoc />
    public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, options?.Headers);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                return;
            }

            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(message.CorrelationId, options?.Headers);
        }

        if (options?.RoutingKey is { } routingKey)
        {
            headers[HeaderKeys.RoutingKey] = routingKey;
        }

        var context = new SendContext
        {
            Message = message,
            MessageType = typeof(T),
            MessageBytes = messageBytes,
            Headers = headers,
            EndPoint = null,
            RoutingKey = options?.RoutingKey,
            Operation = SendOperation.Publish,
        };
        await _sendPipeline.ExecutePublishMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        // Reject ambiguous routing up front. Setting both EndPoint and EndPoints
        // expresses two different routing intents; picking one silently could
        // reroute traffic (for example, after a typo in the option name or a
        // merge of two config paths) with no exception and no log. Require the
        // caller to pick one.
        if (options is { EndPoint.Length: > 0, EndPoints.Count: > 0 })
        {
            throw new ArgumentException(
                "SendOptions.EndPoint and SendOptions.EndPoints cannot both be set. Provide one or the other.",
                nameof(options));
        }

        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, options?.Headers);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                return;
            }

            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(message.CorrelationId, options?.Headers);
        }

        if (options?.EndPoints is { Count: > 0 } endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                var context = new SendContext
                {
                    Message = message,
                    MessageType = typeof(T),
                    MessageBytes = messageBytes,
                    Headers = headers,
                    EndPoint = endpoint,
                    RoutingKey = null,
                    Operation = SendOperation.Send,
                };
                await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var context = new SendContext
            {
                Message = message,
                MessageType = typeof(T),
                MessageBytes = messageBytes,
                Headers = headers,
                EndPoint = options?.EndPoint,
                RoutingKey = null,
                Operation = SendOperation.Send,
            };
            await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            // Serialize once here so outgoing filters can inspect the wire body via the envelope.
            // RequestReplyManager will serialize again on its own path; the cost is one extra
            // serialize per filter-enabled request, kept localized to this branch.
            var messageBytes = _serializer.Serialize(message);
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, requestOptions.Headers);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                throw new InvalidOperationException("Outgoing filters blocked the request message.");
            }

            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(message.CorrelationId, requestOptions.Headers);
        }

        return await _requestReplyManager.SendRequestAsync<TRequest, TReply>(
            message,
            headers,
            requestOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            // See SendRequestAsync for why we serialize locally only on the filter branch.
            var messageBytes = _serializer.Serialize(message);
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, requestOptions.Headers);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                throw new InvalidOperationException("Outgoing filters blocked the request message.");
            }

            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(message.CorrelationId, requestOptions.Headers);
        }

        return await _requestReplyManager.SendRequestMultiAsync<TRequest, TReply>(
            message,
            headers,
            requestOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;

        if (!string.IsNullOrEmpty(requestOptions.EndPoint) || requestOptions.EndPoints is { Count: > 0 })
        {
            throw new ArgumentException("PublishRequestAsync does not support EndPoint or EndPoints. Use SendRequestAsync or SendRequestMultiAsync instead.", nameof(options));
        }

        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            // See SendRequestAsync for why we serialize locally only on the filter branch.
            var messageBytes = _serializer.Serialize(message);
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, requestOptions.Headers);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                throw new InvalidOperationException("Outgoing filters blocked the request message.");
            }

            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(message.CorrelationId, requestOptions.Headers);
        }

        await _requestReplyManager.PublishRequestAsync<TRequest, TReply>(
            message,
            headers,
            requestOptions,
            onReply,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (destinations == null || destinations.Count == 0)
        {
            throw new ArgumentException("At least one destination is required.", nameof(destinations));
        }

        var firstDestination = destinations[0];
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                return;
            }

            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(message.CorrelationId, null);
        }

        if (destinations.Count > 1)
        {
            headers[HeaderKeys.RoutingSlip] = BuildRoutingSlip(destinations);
        }

        var context = new SendContext
        {
            Message = message,
            MessageType = typeof(T),
            MessageBytes = messageBytes,
            Headers = headers,
            EndPoint = firstDestination,
            RoutingKey = null,
            Operation = SendOperation.Send,
        };
        await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
    {
        ThrowIfDisposed();
        if (_producer == null)
        {
            throw new InvalidOperationException("No producer registered. Cannot create stream.");
        }

        return new MessageBusWriteStream(_producer, endpoint, typeof(T));
    }

    /// <inheritdoc />
    public async Task StartConsumingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(_queueConfig.QueueName))
        {
            throw new InvalidOperationException(
                "QueueName is not set. Configure via ServiceConnectBuilder.ConfigureQueues(q => q.QueueName = \"...\") before starting consumption (E-07).");
        }

        try
        {
            await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Dispose won the race between ThrowIfDisposed and WaitAsync — surface as a typed Bus
            // disposal so callers see one exception type, not a raw SemaphoreSlim disposal.
            throw new ObjectDisposedException(typeof(Bus).FullName);
        }

        try
        {
            ThrowIfDisposed(); // re-check: Dispose may have completed after we acquired the semaphore

            IConsumer localConsumer;
            List<string> messageTypeNames;

            lock (_stateLock)
            {
                if (_stopped)
                {
                    throw new InvalidOperationException(
                        "Bus has been stopped; dispose it and create a new Bus instance to resume consuming.");
                }

                if (_consuming)
                {
                    throw new InvalidOperationException("Already consuming.");
                }

                if (_consumer == null)
                {
                    throw new InvalidOperationException("No consumer registered. Call UseRabbitMQ() or register an IConsumer.");
                }

                var typeNameSet = new HashSet<string>(_handlerReferences.Count, StringComparer.Ordinal);
                foreach (var h in _handlerReferences)
                {
                    typeNameSet.Add(MessageTypeExchangeName.From(h.MessageType));
                }

                messageTypeNames = [.. typeNameSet];

                localConsumer = _consumer;
            }

            _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
                _queueConfig.QueueName, messageTypeNames.Count);

            await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.DispatchAsync, cancellationToken).ConfigureAwait(false);

            lock (_stateLock) { _consuming = true; }
        }
        finally
        {
            // Guard against the semaphore being disposed by a concurrent DisposeAsync that won the
            // race after WaitAsync returned. A disposed-semaphore Release is benign here — we are
            // already exiting — so swallow any ObjectDisposedException to avoid masking the real cause.
            try { _lifecycleSemaphore.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <inheritdoc />
    public async Task StopConsumingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopConsumingCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RequestTimeoutAsync(Guid correlationId, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_timeoutStore is null)
        {
            throw new InvalidOperationException("No ITimeoutStore is registered. Add persistence via UseInMemoryPersistence() or UseMongoDbPersistence() and set BusConfiguration.EnableProcessManagerTimeouts = true.");
        }

        if (delay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Timeout delay must be positive.");
        }

        var data = new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = _queueConfig.QueueName,
            ProcessManagerId = correlationId,
            Time = DateTimeOffset.UtcNow + delay,
            Headers = TimeoutHeaderPersistence.CaptureForStorage(_consumeContextAccessor.CurrentHeaders)
        };

        await _timeoutStore.InsertTimeoutAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops consuming without the disposed guard. Called from DisposeAsync,
    /// which sets _disposed = true before invoking this -- a ThrowIfDisposed()
    /// here would throw ObjectDisposedException and prevent clean shutdown.
    /// Public callers must use StopConsumingAsync instead, which adds the guard.
    /// </summary>
    private async Task StopConsumingCoreAsync(CancellationToken cancellationToken = default)
    {
        // Acquire the lifecycle semaphore — this may throw OperationCanceledException if
        // the caller cancels before the wait completes. In that case we fall through to the
        // catch below to still tear down any in-flight consumer before rethrowing.
        bool semaphoreAcquired = false;
        IConsumer? localConsumer = null;
        OperationCanceledException? pendingCancellation = null;

        try
        {
            await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            semaphoreAcquired = true;
        }
        catch (OperationCanceledException oce)
        {
            pendingCancellation = oce;
        }
        catch (ObjectDisposedException)
        {
            // The semaphore was disposed by a concurrent DisposeAsync — translate to a typed
            // Bus disposal so callers see a consistent exception type rather than a raw semaphore disposal.
            throw new ObjectDisposedException(typeof(Bus).FullName);
        }

        try
        {
            lock (_stateLock)
            {
                _logger.LogInformation("Bus stopping message consumption.");
                if (_consuming)
                {
                    _consuming = false;
                    localConsumer = _consumer;
                    // Stop is terminal only when consumption actually ran: the shared
                    // consumer is disposed and cannot be restarted. Mark the bus stopped
                    // so attempted restarts throw a clear error instead of silently failing.
                    // A defensive stop on a bus that never started must leave it restartable.
                    _stopped = true;
                }
            }

            if (localConsumer != null)
            {
                if (pendingCancellation != null)
                {
                    // Cancellation already signalled — dispose immediately without a grace-period wait.
                    try
                    {
                        await localConsumer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception disposeEx)
                    {
                        _logger.LogWarning(disposeEx, "Consumer dispose failed during cancellation.");
                    }
                }
                else
                {
                    try
                    {
                        await localConsumer.DisposeAsync().AsTask().WaitAsync(_disposeTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation arrived after we acquired the semaphore — still dispose.
                        try { await localConsumer.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception disposeEx) { _logger.LogWarning(disposeEx, "Consumer dispose failed during cancellation."); }
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Timed out waiting {Timeout} for consumer disposal.", _disposeTimeout);
                    }
                }
            }
        }
        finally
        {
            // Guard against the semaphore being disposed by a concurrent DisposeAsync that won the
            // race after WaitAsync returned. A disposed-semaphore Release is benign here — we are
            // already exiting — so swallow any ObjectDisposedException to avoid masking the real cause.
            if (semaphoreAcquired)
            {
                try { _lifecycleSemaphore.Release(); }
                catch (ObjectDisposedException) { }
            }
        }

        if (pendingCancellation != null)
        {
            throw pendingCancellation;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopConsumingCoreAsync().ConfigureAwait(false);
        await _sendPipeline.DisposeAsync().ConfigureAwait(false);
        if (_producer != null)
        {
            await _producer.DisposeAsync().ConfigureAwait(false);
        }

        _lifecycleSemaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(typeof(Bus).FullName);
        }
    }

    // Outgoing filters share the scoped-pipeline contract with inbound filters and
    // middleware: a fresh per-send DI scope is pushed through ConsumeScopeAccessor so
    // scoped/transient filter dependencies are honoured instead of being leaked via
    // the root provider. The scope is disposed as soon as the filter chain completes.
    private async Task<FilterAction> RunOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        using var _ = _scopeAccessor.Push(scope.ServiceProvider);
        return await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Header keys that the Bus stamps authoritatively. Caller-supplied values for
    /// any of these keys are silently ignored so the bus remains the single source
    /// of truth for message identity.
    /// </summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.Ordinal)
    {
        HeaderKeys.CorrelationId,
        HeaderKeys.MessageId,
    };

    private Envelope CreateEnvelope(byte[] body, Guid correlationId, IReadOnlyDictionary<string, string>? additionalHeaders = null)
    {
        // Snapshot once up front so a concurrent caller mutating the source
        // dictionary can't throw "Collection was modified" inside the foreach
        // below. The type parameter being IReadOnlyDictionary signals intent
        // but doesn't prevent external mutation through the original reference.
        var snapshot = additionalHeaders?.ToArray();

        var envelope = new Envelope
        {
            Body = body,
            Headers = new Dictionary<string, object>(StringComparer.Ordinal)
        };

        if (snapshot is not null)
        {
            foreach (var header in snapshot)
            {
                if (ReservedHeaders.Contains(header.Key))
                {
                    _logger.LogWarning("Caller-supplied reserved header '{Key}' will be overwritten by the framework", header.Key);
                    continue;
                }
                envelope.Headers[header.Key] = header.Value;
            }
        }

        // Bus-authoritative: stamp system headers last so callers cannot spoof via options.Headers.
        // Outgoing filters and middleware rely on MessageId / CorrelationId being present. MessageType
        // is stamped authoritatively by the producer (OutboundHeaderBuilder) as the operation name
        // "Publish"|"Send"|"ByteStream"; type info is carried by TypeName / FullTypeName.
        envelope.Headers[HeaderKeys.CorrelationId] = correlationId.ToString();
        envelope.Headers[HeaderKeys.MessageId] = Guid.NewGuid().ToString();

        return envelope;
    }

    private static Dictionary<string, string> ExtractHeaders(Envelope envelope)
    {
        // Pre-size the destination to the known envelope header count so the
        // dictionary is not rehashed as we fill it.
        var headers = new Dictionary<string, string>(envelope.Headers.Count, StringComparer.Ordinal);
        foreach (var kvp in envelope.Headers)
        {
            headers[kvp.Key] = kvp.Value switch
            {
                null => string.Empty,
                string s => s,
                _ => kvp.Value.ToString() ?? string.Empty
            };
        }
        return headers;
    }

    private static string BuildRoutingSlip(IList<string> destinations)
    {
        if (destinations.Count <= 1)
        {
            return string.Empty;
        }

        // destinations[0] is the immediate send target; the routing slip describes
        // the *subsequent* hops, so the loop deliberately starts at index 1.
        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < destinations.Count; index++)
        {
            if (index > 1)
            {
                builder.Append(',');
            }

            builder.Append(destinations[index]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fast-path header builder used when no outgoing filters are registered.
    /// Produces the same <see cref="Dictionary{TKey,TValue}"/> that
    /// <see cref="CreateEnvelope"/> + <see cref="ExtractHeaders"/> would return,
    /// without allocating the intermediate <see cref="Envelope"/> or its
    /// <c>Dictionary&lt;string, object&gt;</c> headers map.
    /// </summary>
    private Dictionary<string, string> BuildHeadersDirect(Guid correlationId, IReadOnlyDictionary<string, string>? additionalHeaders)
    {
        // Snapshot-then-iterate: the caller still holds a reference to the
        // underlying dictionary, so a concurrent mutation during the foreach
        // below would throw "Collection was modified". ToArray grabs a stable
        // copy with a single enumeration.
        var snapshot = additionalHeaders?.ToArray();
        var capacity = ReservedHeaders.Count + (snapshot?.Length ?? 0);
        var headers = new Dictionary<string, string>(capacity, StringComparer.Ordinal);

        if (snapshot is not null)
        {
            foreach (var kvp in snapshot)
            {
                // Skip reserved keys — the bus stamps these authoritatively below.
                if (ReservedHeaders.Contains(kvp.Key))
                {
                    _logger.LogWarning("Caller-supplied reserved header '{Key}' will be overwritten by the framework", kvp.Key);
                    continue;
                }
                headers[kvp.Key] = kvp.Value;
            }
        }

        // Bus-authoritative: stamp system headers last so callers cannot spoof via options.Headers.
        // MessageType is not stamped here; OutboundHeaderBuilder is the sole authoritative stamper
        // of the operation name on the wire.
        headers[HeaderKeys.CorrelationId] = correlationId.ToString();
        headers[HeaderKeys.MessageId] = Guid.NewGuid().ToString();

        return headers;
    }
}
