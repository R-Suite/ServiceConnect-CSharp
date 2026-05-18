using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Diagnostics;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;

namespace ServiceConnect;

/// <summary>
/// Default <see cref="IBus"/> implementation that coordinates serialization, filtering,
/// transport dispatch, request-reply tracking, and message consumption.
/// </summary>
internal sealed class Bus : IBus
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly ISendMessagePipeline _sendPipeline;
    private readonly IRequestReplyManager _requestReplyManager;
    private readonly ILogger<Bus> _logger;
    private readonly IQueueConfiguration _queueConfig;
    private readonly IMessageDispatcher _dispatcher;
    private readonly IReadOnlyList<HandlerReference> _handlerReferences;
    private readonly IConsumer? _consumer;
    private readonly IProducer? _producer;
    private readonly ITimeoutStore? _timeoutStore;
    private readonly IConsumeContextAccessor _consumeContextAccessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConsumeScopeAccessor _scopeAccessor;
    private readonly IBusConfiguration _busConfig;
    private readonly TimeProvider _timeProvider;
    private readonly bool _hasOutgoingFilters;
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
        IReadOnlyList<HandlerReference> handlerReferences,
        IPipelineConfiguration pipelineConfig,
        IServiceScopeFactory scopeFactory,
        IConsumeScopeAccessor scopeAccessor,
        IConsumer? consumer = null,
        IProducer? producer = null,
        ITimeoutStore? timeoutStore = null,
        IConsumeContextAccessor? consumeContextAccessor = null,
        IBusConfiguration? busConfig = null,
        TimeProvider? timeProvider = null)
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
        _timeoutStore = timeoutStore;
        _consumeContextAccessor = consumeContextAccessor ?? new ConsumeContextAccessor();
        // busConfig is optional for test call sites; production always supplies it via ServiceCollectionExtensions.
        // When absent, fall back to the standard 30-second dispose timeout so the safety bound still applies.
        _busConfig = busConfig ?? new Configuration.BusConfiguration();
        // TimeProvider is optional so test call sites can construct a Bus without DI; production
        // wiring threads sp.GetService<TimeProvider>() through. RequestTimeoutAsync uses this so
        // FakeTimeProvider-driven tests of process-manager scenarios match the wall-clock
        // semantics of the rest of the time-dependent surface (timeout store, header timestamps).
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>
    /// True only when (a) the bus has started consuming, (b) the broker has not
    /// cancelled the consumer (basic.cancel: queue deleted, policy expired, mirror
    /// promoted), and (c) the bus has not started disposing. The dispose check uses
    /// _disposed (set under Interlocked.Exchange in DisposeAsync) which becomes
    /// visible immediately at the moment dispose is initiated, without depending on
    /// the subsequent _consuming = false write inside StopConsumingCoreAsync.
    /// </remarks>
    public bool IsConsuming =>
        _consuming
        && Volatile.Read(ref _disposed) == 0
        && !(_consumer?.IsCancelledByBroker ?? false);

    /// <inheritdoc />
    public bool IsCancelledByBroker => _consumer?.IsCancelledByBroker ?? false;

    /// <inheritdoc />
    /// <remarks>
    /// True once StopConsumingAsync has flipped _stopped, OR DisposeAsync has flipped
    /// _disposed. Both transitions are latched (never reset), so this signal correctly
    /// distinguishes "intentional shutdown" from "transient disconnect / pre-start" — the
    /// health check uses it to bypass the recovery-grace window on shutdown.
    /// </remarks>
    public bool IsStopped => Volatile.Read(ref _stopped) || Volatile.Read(ref _disposed) != 0;

    /// <inheritdoc />
    public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var prep = await PrepareOutboundAsync(message, options?.Headers, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            return;
        }
        var messageBytes = prep.Bytes;
        var headers = prep.Headers;

        if (options?.RoutingKey is { } routingKey)
        {
            headers[HeaderKeys.RoutingKey] = routingKey;
        }

        // Resolve the effective routing key: caller-supplied options take precedence, but
        // an outgoing IFilter that wrote HeaderKeys.RoutingKey into the envelope (the
        // pre-extract path) should also reach the AMQP basic.publish routing-key slot.
        // Without this read-back, filter-mutated routing keys are stamped onto the wire
        // headers but the producer's BasicPublishAsync still passes empty-string for
        // routing-key, so topic-exchange dispatch is silently dropped.
        string? effectiveRoutingKey = options?.RoutingKey;
        if (effectiveRoutingKey is null && headers.TryGetValue(HeaderKeys.RoutingKey, out var headerRoutingKey) && !string.IsNullOrEmpty(headerRoutingKey))
        {
            effectiveRoutingKey = headerRoutingKey;
        }

        var context = new SendContext
        {
            Message = message,
            MessageType = typeof(T),
            MessageBytes = messageBytes,
            Headers = headers,
            EndPoint = null,
            RoutingKey = effectiveRoutingKey,
            Operation = SendOperation.Publish,
        };
        await _sendPipeline.ExecutePublishMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var prep = await PrepareOutboundAsync(message, options?.Headers, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            return;
        }
        var messageBytes = prep.Bytes;
        var headers = prep.Headers;

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

    /// <inheritdoc />
    public async Task SendToManyAsync<T>(T message, IReadOnlyList<string> endPoints, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(endPoints);
        if (endPoints.Count == 0)
        {
            throw new ArgumentException("SendToManyAsync requires at least one endpoint.", nameof(endPoints));
        }

        var prep = await PrepareOutboundAsync(message, options?.Headers, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            return;
        }
        var messageBytes = prep.Bytes;
        var headers = prep.Headers;

        List<Exception>? endpointFailures = null;
        foreach (var endpoint in endPoints)
        {
            // Per-iteration shallow copy: ISendMessageMiddleware writes to ctx.Headers
            // (telemetry stamps, signing, dedup keys) and per-endpoint mutations would
            // otherwise leak into subsequent iterations of this fan-out loop. The copy
            // is O(n) on header count (typically < 10 entries); negligible per-message.
            var perEndpointHeaders = new Dictionary<string, string>(headers, StringComparer.Ordinal);

            var context = new SendContext
            {
                Message = message,
                MessageType = typeof(T),
                MessageBytes = messageBytes,
                Headers = perEndpointHeaders,
                EndPoint = endpoint,
                RoutingKey = null,
                Operation = SendOperation.Send,
            };
            try
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested)
            {
                // Caller-initiated cancellation mid-fan-out must surface the OCE (callers expect to
                // detect cancellation), but any failures already accumulated for prior endpoints
                // would otherwise be silently dropped. Wrap them with the OCE so the caller sees
                // both: AggregateException's InnerExceptions enumeration starts with the OCE for
                // OCE-shape detection upstream.
                //
                // The `when (cancellationToken.IsCancellationRequested)` filter is load-bearing:
                // an OCE thrown from a middleware-internal linked CTS (custom timeout, per-endpoint
                // deadline) carries a different token and is NOT caller cancellation. Those fall
                // through to the generic catch and aggregate as endpoint failures, matching the
                // semantics of Producer.SendAsync's per-endpoint loop.
                if (endpointFailures is { Count: > 0 })
                {
                    var combined = new List<Exception>(endpointFailures.Count + 1) { oce };
                    combined.AddRange(endpointFailures);
                    throw new AggregateException(
                        $"SendToManyAsync of message type '{typeof(T).FullName}' was cancelled after one or more endpoint failures.",
                        combined);
                }
                throw;
            }
            catch (ObjectDisposedException ode)
            {
                // The send pipeline (or one of its components) was disposed by a concurrent
                // shutdown. Every remaining iteration would throw the same ODE; aggregating
                // N identical ODEs hides the real cause behind a list of duplicates. Mirror
                // Producer.SendAsync's per-endpoint loop and surface the ODE directly,
                // wrapping any failures collected on prior endpoints so they aren't lost.
                if (endpointFailures is { Count: > 0 })
                {
                    var combined = new List<Exception>(endpointFailures.Count + 1) { ode };
                    combined.AddRange(endpointFailures);
                    throw new AggregateException(
                        $"SendToManyAsync of message type '{typeof(T).FullName}' aborted after dispose with one or more endpoint failures.",
                        combined);
                }
                throw;
            }
            catch (Exception ex)
            {
                (endpointFailures ??= []).Add(ex);
            }
        }

        if (endpointFailures is { Count: > 0 })
        {
            throw new AggregateException(
                $"One or more endpoints failed during SendToManyAsync of message type '{typeof(T).FullName}'.",
                endpointFailures);
        }
    }

    /// <inheritdoc />
    public async Task<TReply> SendRequestAsync<TRequest, TReply>(TRequest message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;
        var prep = await PrepareOutboundForRequestAsync(message, requestOptions.Headers, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            throw new OutgoingFiltersBlockedException("Outgoing filters blocked the request message.");
        }
        var headers = prep.Headers;

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
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;
        var prep = await PrepareOutboundForRequestAsync(message, requestOptions.Headers, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            throw new OutgoingFiltersBlockedException("Outgoing filters blocked the request message.");
        }
        var headers = prep.Headers;

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
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(onReply);
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;

        if (!string.IsNullOrEmpty(requestOptions.EndPoint))
        {
            throw new ArgumentException("PublishRequestAsync does not support EndPoint. Use SendRequestAsync for single-destination requests.", nameof(options));
        }

        var prep = await PrepareOutboundForRequestAsync(message, requestOptions.Headers, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            throw new OutgoingFiltersBlockedException("Outgoing filters blocked the request message.");
        }
        var headers = prep.Headers;

        await _requestReplyManager.PublishRequestAsync<TRequest, TReply>(
            message,
            headers,
            requestOptions,
            onReply,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RouteAsync<T>(T message, IReadOnlyList<string> destinations, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinations);

        // Snapshot to defend against caller mutation between validation and use.
        var snapshot = destinations.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(
                "RouteAsync requires at least one destination.",
                nameof(destinations));
        }
        for (int i = 0; i < snapshot.Length; i++)
        {
            // Comma is the in-header separator for the routing-slip; reject explicitly so
            // the error names the structural cause rather than the generic "reserved char".
            if (snapshot[i] != null && snapshot[i].Contains(','))
            {
                throw new ArgumentException(
                    $"Destination at index {i} contains a comma ('{snapshot[i]}'); commas are reserved as the routing-slip separator.",
                    nameof(destinations));
            }
            // Receive-side ForwardRoutingSlipAsync rejects the same set of characters / lengths
            // and logs+drops the message. Mirror the validator on the send side so producers
            // fail fast with a typed ArgumentException instead of stalling on an in-flight
            // message that nack/dead-letters at the next hop with no caller signal.
            var failure = RoutingSlipDestinationValidator.GetFailureReason(snapshot[i]);
            if (failure is not null)
            {
                throw new ArgumentException(
                    $"Destination at index {i} ('{snapshot[i]}') is invalid: {failure}.",
                    nameof(destinations));
            }
        }

        var firstDestination = snapshot[0];
        var prep = await PrepareOutboundAsync(message, null, cancellationToken).ConfigureAwait(false);
        if (prep.Stopped)
        {
            return;
        }
        var messageBytes = prep.Bytes;
        var headers = prep.Headers;

        if (snapshot.Length > 1)
        {
            headers[HeaderKeys.RoutingSlip] = BuildRoutingSlip(snapshot);
        }

        // Cross-service hop counter. Each RouteAsync hop — whether driven by the framework's
        // own ForwardRoutingSlipAsync or by a handler that explicitly invokes RouteAsync —
        // increments the inbound counter (defaulting to 0 for the first hop in a flow) and
        // stamps it on the outbound headers. If the total exceeds MaxRoutingSlipHops, the
        // forward is refused. Without this, a service that receives a near-end-of-slip
        // message could publish a fresh 32-entry slip and amplify the flow indefinitely
        // across services; the per-slip cap in HandlerProcessor only bounds one hop's slip
        // length, not the total flow.
        var hopsCompleted = ReadInboundHopsCompleted();
        var outboundHops = hopsCompleted + 1;
        if (outboundHops > _busConfig.MaxRoutingSlipHops)
        {
            throw new InvalidOperationException(
                $"Total routing-slip hops ({outboundHops}) exceeds the configured MaxRoutingSlipHops cap ({_busConfig.MaxRoutingSlipHops}); " +
                "rejecting forward to prevent cross-service amplification.");
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
            RoutingSlipHopsCompleted = outboundHops,
        };
        await _sendPipeline.ExecuteSendMessagePipelineAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private int ReadInboundHopsCompleted()
    {
        var inboundHeaders = _consumeContextAccessor.CurrentHeaders;
        if (inboundHeaders is null ||
            !inboundHeaders.TryGetValue(HeaderKeys.RoutingSlipHopsCompleted, out var raw))
        {
            return 0;
        }
        var decoded = HeaderDecoder.Decode(raw);
        if (string.IsNullOrEmpty(decoded) ||
            !int.TryParse(decoded, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hops) ||
            hops < 0)
        {
            return 0;
        }
        // Clamp to MaxRoutingSlipHops so `hops + 1` on the caller's side never overflows.
        // Without this, a crafted inbound header carrying int.MaxValue wraps to int.MinValue
        // and slips past the `outboundHops > MaxRoutingSlipHops` guard — the per-hop cap
        // is the framework's only cross-service amplification control, so silent overflow
        // is a real bypass, not theory.
        return Math.Min(hops, _busConfig.MaxRoutingSlipHops);
    }

    /// <inheritdoc />
    public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
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
                "QueueName is not set. Configure via ServiceConnectBuilder.ConfigureQueues(q => q.QueueName = \"...\") before starting consumption.");
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

            // Flip _consuming = true BEFORE the await so health checks during the StartConsumingAsync
            // window see Healthy. If the flag flipped after the await, broker dispatch could arrive
            // in the gap and IsConsuming would return false during a perfectly-fine startup,
            // surfacing as spurious health-check Unhealthy. Wrap the await in try/catch to roll
            // the flag back on failure (the broker isn't actually consuming).
            lock (_stateLock) { _consuming = true; }
            try
            {
                // ConsumerEventHandler passes IDictionary<string,object>; DispatchAsync accepts
                // IReadOnlyDictionary<string,object>. The transport always supplies Dictionary<,>
                // (which implements both) so the as-cast succeeds on the hot path; the fallback
                // copy handles any non-Dictionary<,> transport implementation.
                await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames,
                    (msg, type, hdrs, ct) => _dispatcher.DispatchAsync(msg, type,
                        hdrs as IReadOnlyDictionary<string, object>
                            ?? new Dictionary<string, object>(hdrs, StringComparer.Ordinal),
                        ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (_stateLock) { _consuming = false; }
                throw;
            }
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
    /// <remarks>
    /// Idempotent on a disposed bus. The host's BusHostedService.StopAsync may run
    /// AFTER the bus has been disposed by another shutdown path; throwing here
    /// surfaced as a noisy ObjectDisposedException log on every shutdown. A disposed
    /// bus is also a stopped bus (DisposeAsync calls StopConsumingCoreAsync), so the
    /// idempotent contract is correct.
    /// </remarks>
    public async Task StopConsumingAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

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

        // Empty correlation id is always a programmer error: TimeoutMessage dispatch
        // would key on Guid.Empty and IProcessManagerFinder.FindData would never
        // match, leaving a stray timeout row that gets retried-then-dropped. Fail
        // fast so the bug surfaces at the offending call site, not at dispatch time.
        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Timeout correlation id must not be Guid.Empty. Pass the saga's own data.CorrelationId.",
                nameof(correlationId));
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
            Time = _timeProvider.GetUtcNow() + delay,
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
    /// <param name="cancellationToken">Token to cancel the semaphore wait.</param>
    /// <param name="semaphoreWaitTimeout">
    /// When supplied (DisposeAsync's path), the semaphore wait is bounded by this duration.
    /// On timeout, teardown proceeds without the semaphore — the broker connection is about to
    /// be torn down by DI's IServiceProvider disposal anyway, so proceeding is safe. When
    /// absent (StopConsumingAsync's path), the wait blocks until cancellation.
    /// </param>
    private async Task StopConsumingCoreAsync(CancellationToken cancellationToken = default, TimeSpan? semaphoreWaitTimeout = null)
    {
        bool semaphoreAcquired = false;
        try
        {
            // When a timeout is provided (DisposeAsync's path) and the wait does not complete in
            // time, proceed with teardown WITHOUT the semaphore. A concurrent StartConsumingAsync
            // may still be mid-handshake; this is acceptable in dispose because the broker
            // connection is about to be torn down by DI's IServiceProvider disposal anyway.
            if (semaphoreWaitTimeout is { } timeout)
            {
                semaphoreAcquired = await _lifecycleSemaphore.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!semaphoreAcquired)
                {
                    _logger.LogWarning(
                        "Bus.StopConsumingCoreAsync timed out waiting for the lifecycle semaphore after {Timeout}; proceeding with teardown anyway.",
                        timeout);
                }
            }
            else
            {
                await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                semaphoreAcquired = true;
            }

            // Capture the consumer reference under the state lock and the "was actually
            // consuming" flag, then issue a graceful broker stop OUTSIDE the lock. Holding
            // _stateLock around an awaited broker call would block other lifecycle queries
            // (IsConsuming, IsCancelledByBroker) for the full graceful-shutdown timeout.
            IConsumer? consumerToStop = null;
            lock (_stateLock)
            {
                _logger.LogInformation("Bus stopping message consumption.");
                if (_consuming)
                {
                    consumerToStop = _consumer;
                    _consuming = false;
                    // Stop is terminal: the IConsumer singleton is owned by DI and is reused
                    // across the host's lifetime, but once the bus has signalled stop we do
                    // not restart consumption on this Bus instance. Mark the bus stopped so
                    // attempted restarts throw a clear error instead of silently failing.
                    // A defensive stop on a bus that never started must leave it restartable.
                    // Volatile.Write so IsStopped readers (the health check) observe the
                    // latch without acquiring _stateLock.
                    Volatile.Write(ref _stopped, true);
                }
            }

            // Issue the graceful broker stop. The transport BasicCancels each consumer
            // host and drains in-flight handler invocations; without this, the broker
            // keeps delivering messages until DI disposes the consumer (which can be
            // arbitrarily later than BusHostedService.StopAsync returns) and the
            // dispatch pipeline keeps running between BusHostedService.StopAsync and
            // IConsumer.DisposeAsync. Third-party IConsumer impls inherit the no-op
            // default-interface-method, in which case this is a documented no-op.
            if (consumerToStop is not null)
            {
                try
                {
                    await consumerToStop.StopConsumingAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IConsumer.StopConsumingAsync threw during Bus.StopConsumingAsync; broker delivery may continue until consumer dispose.");
                }
            }
            // _consumer.DisposeAsync() is intentionally NOT called here. IConsumer is registered
            // as a DI singleton; the host's IServiceProvider disposes it on host shutdown. The
            // earlier double-dispose path (Bus disposing the transport directly) raced with DI's
            // own teardown and forced a WaitAsync timeout-mask to keep the dispose path bounded.
            // Removing the dispose call removes the timeout-mask path.
        }
        catch (ObjectDisposedException)
        {
            // The semaphore was disposed by a concurrent DisposeAsync — translate to a typed
            // Bus disposal so callers see a consistent exception type rather than a raw semaphore disposal.
            throw new ObjectDisposedException(typeof(Bus).FullName);
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
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop consuming under the lifecycle semaphore. _consumer and _producer are DI singletons;
        // the host's IServiceProvider disposes them when the host shuts down — Bus.DisposeAsync
        // does not double-dispose them. _sendPipeline is owned by the Bus and is disposed here.
        // Pass DisposeTimeout so a wedged StartConsumingAsync (broker partition mid-handshake)
        // does not block container shutdown indefinitely.
        try
        {
            await StopConsumingCoreAsync(semaphoreWaitTimeout: _busConfig.DisposeTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bus.StopConsumingCoreAsync failed during dispose.");
        }

        try
        {
            await _sendPipeline.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Without this catch, a throw here would skip the request-reply-manager dispose
            // below, leaving every in-flight SendRequestAsync TCS un-faulted — callers
            // awaiting with Timeout.Infinite would never wake. Today _sendPipeline.DisposeAsync
            // only flips a flag and cannot throw, but a future implementation (or third-party
            // ISendMessagePipeline) might; the guard matches the neighbouring catch shapes.
            _logger.LogWarning(ex, "SendMessagePipeline.DisposeAsync failed during bus shutdown.");
        }

        // Fault any in-flight request TCSes so callers awaiting a reply (especially with
        // Timeout.Infinite) wake up promptly on shutdown rather than waiting for GC. The
        // concrete RequestReplyManager implements IAsyncDisposable; IRequestReplyManager
        // does not (custom third-party impls don't have to opt in). Pattern-match to honour
        // it when present.
        if (_requestReplyManager is IAsyncDisposable disposableReplyManager)
        {
            try
            {
                await disposableReplyManager.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RequestReplyManager.DisposeAsync failed during bus shutdown.");
            }
        }

        // _lifecycleSemaphore is intentionally NOT Disposed:
        // SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and we never call
        // AvailableWaitHandle, so disposal is a functional no-op. A concurrent caller's Release()
        // on a disposed semaphore would throw ObjectDisposedException out of the unwind path,
        // which we cannot prevent without holding GC references to every caller. Mirrors the
        // Connection / ProducerConnection / Producer "do not dispose the semaphore" pattern.
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(typeof(Bus).FullName);
        }
    }

    // Outgoing filters share the scoped-pipeline contract with inbound filters and
    // middleware: a fresh per-send DI scope is pushed through IConsumeScopeAccessor so
    // scoped/transient filter dependencies are honoured instead of being leaked via
    // the root provider. The scope is disposed as soon as the filter chain completes.
    private async Task<FilterAction> RunOutgoingFiltersAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        // CreateAsyncScope so user-supplied IFilter / ISendMessageMiddleware implementations
        // that are IAsyncDisposable-only (no IDisposable) are honoured. Explicit try/finally
        // + DisposeAsync().ConfigureAwait(false) so the analyzer can see the await.
        var scope = _scopeFactory.CreateAsyncScope();
        try
        {
            using var _ = _scopeAccessor.Push(scope.ServiceProvider);
            var action = await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false);
            // A filter-Stop short-circuits the call before the send-message middleware runs,
            // so no publish/send span is emitted and the operator-side trace shows a silent
            // gap. Surface that case on a dedicated counter so dashboards can alert on
            // filter-suppressed deliveries without parsing logs. Tagged with the message
            // type name (when known) so per-shape suppression rates are visible.
            if (action == FilterAction.Stop)
            {
                // String literal rather than a const from ServiceConnect.Telemetry — keeps the
                // ServiceConnect package from taking a build-time dependency on the optional
                // Telemetry package just to reference its attribute-name constants. The tag
                // schema matches what Telemetry emits on the corresponding success path.
                var tags = new System.Diagnostics.TagList
                {
                    { "messaging.system", "serviceconnect" },
                };
                if (envelope.Headers.TryGetValue(HeaderKeys.TypeName, out var typeNameObj) && typeNameObj is string typeName && !string.IsNullOrEmpty(typeName))
                {
                    tags.Add("messaging.message.type", typeName);
                }
                ServiceConnectMeter.AddOutgoingFiltersBlocked(tags);
            }
            return action;
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Header keys that the Bus stamps authoritatively. Caller-supplied values for
    /// any of these keys are silently ignored so the bus remains the single source
    /// of truth for message identity.
    /// </summary>
    // StringComparer.Ordinal (case-sensitive) — AMQP wire-header names are
    // case-sensitive per spec; matching with OrdinalIgnoreCase would treat
    // "MessageId" and "messageid" as the same key when a malformed producer
    // could be sending both.
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.Ordinal)
    {
        HeaderKeys.CorrelationId,
        HeaderKeys.MessageId,
    };

    private Envelope CreateEnvelope(ReadOnlyMemory<byte> body, Guid correlationId, Type messageType, IReadOnlyDictionary<string, string>? additionalHeaders = null)
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
        // Outgoing filters and middleware rely on MessageId / CorrelationId being present.
        envelope.Headers[HeaderKeys.CorrelationId] = correlationId.ToString();
        envelope.Headers[HeaderKeys.MessageId] = Guid.NewGuid().ToString();
        // Stamp type-name headers here so outgoing filters can gate on message type
        // (e.g. drop telemetry control messages, route by message-type). The producer's
        // OutboundHeaderBuilder re-stamps these authoritatively with identical values
        // from its TypeNameCache (TypeName = FullName, FullTypeName = AssemblyQualifiedName),
        // so the producer values still win on the wire — but the outgoing filter pipeline
        // now sees a complete header set instead of just CorrelationId+MessageId.
        if (messageType.FullName is { } fullName)
        {
            envelope.Headers[HeaderKeys.TypeName] = fullName;
        }
        if (messageType.AssemblyQualifiedName is { } aqn)
        {
            envelope.Headers[HeaderKeys.FullTypeName] = aqn;
        }

        return envelope;
    }

    private static Dictionary<string, string> ExtractHeaders(Envelope envelope)
    {
        // Pre-size the destination to the known envelope header count so the
        // dictionary is not rehashed as we fill it.
        var headers = new Dictionary<string, string>(envelope.Headers.Count, StringComparer.Ordinal);
        foreach (var kvp in envelope.Headers)
        {
            // IFormattable handles every BCL value type (decimal/double/float/DateTime/
            // DateTimeOffset/TimeSpan/Guid/int/long/…) with explicit InvariantCulture, so
            // a German producer's `(3.14m).ToString()` does not stamp `"3,14"` on the wire
            // for an invariant-parsing consumer to read as the wrong number. Object types
            // without `IFormattable` fall through to a naked ToString — for those, the
            // caller is responsible for using a culture-invariant representation if the
            // value crosses the wire to a different locale.
            headers[kvp.Key] = kvp.Value switch
            {
                null => string.Empty,
                string s => s,
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => kvp.Value.ToString() ?? string.Empty
            };
        }
        return headers;
    }

    private static string BuildRoutingSlip(IReadOnlyList<string> destinations)
    {
        if (destinations.Count <= 1)
        {
            return string.Empty;
        }

        // RouteAsync's caller-validation already screened these. Today RouteAsync is the only
        // caller of BuildRoutingSlip, but the slip's comma-separated wire format is non-recoverable
        // on the receiving side; revalidate here as defence in depth so a future internal caller
        // can't accidentally bypass the check.
        for (int i = 0; i < destinations.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(destinations[i]))
            {
                throw new ArgumentException(
                    $"Destination at index {i} is null or whitespace.",
                    nameof(destinations));
            }
            if (destinations[i].Contains(','))
            {
                throw new ArgumentException(
                    $"Destination at index {i} contains a comma; commas are reserved as the routing-slip separator.",
                    nameof(destinations));
            }
        }

        // destinations[0] is the immediate send target; the routing slip describes
        // the *subsequent* hops, so the join deliberately starts at index 1.
        return string.Join(',', destinations.Skip(1));
    }

    /// <summary>
    /// Result of the outbound preamble: serialised wire bytes, the headers dictionary to attach,
    /// and a flag indicating whether an outgoing filter requested the message be dropped.
    /// Callers MUST check <see cref="Stopped"/> before reading <see cref="Bytes"/> or <see cref="Headers"/>;
    /// the latter two are undefined when the filter pipeline stopped the message.
    /// </summary>
    internal readonly record struct OutboundPreparation(
        ReadOnlyMemory<byte> Bytes,
        Dictionary<string, string> Headers,
        bool Stopped);

    /// <summary>
    /// Runs the outbound preamble shared by Publish/Send/SendToMany/Route: serialise the message,
    /// then either build headers directly (no outgoing filters configured) or build an envelope,
    /// invoke the outgoing-filter pipeline, and extract headers from the envelope.
    /// </summary>
    /// <remarks>
    /// The helper always serialises because all callers need the wire bytes for the downstream
    /// send pipeline. Request paths use a separate helper that conditionally serialises because
    /// <c>RequestReplyManager</c> re-serialises downstream.
    /// </remarks>
    internal async Task<OutboundPreparation> PrepareOutboundAsync<T>(
        T message,
        IReadOnlyDictionary<string, string>? callerHeaders,
        CancellationToken cancellationToken) where T : Message
    {
        var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
        _serializer.Serialize(message, bufferWriter);
        var messageBytes = bufferWriter.WrittenMemory;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, typeof(T), callerHeaders);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                return new OutboundPreparation(default, null!, Stopped: true);
            }
            return new OutboundPreparation(messageBytes, ExtractHeaders(envelope), Stopped: false);
        }

        return new OutboundPreparation(messageBytes, BuildHeadersDirect(message.CorrelationId, callerHeaders), Stopped: false);
    }

    /// <summary>
    /// Result of the outbound preamble for request paths: the headers to attach and a flag
    /// indicating whether an outgoing filter requested the request be blocked. Request paths
    /// re-serialise the message downstream in <c>RequestReplyManager</c>, so this helper
    /// does not return wire bytes; callers MUST check <see cref="Stopped"/> before reading
    /// <see cref="Headers"/>.
    /// </summary>
    internal readonly record struct RequestPreparation(
        Dictionary<string, string> Headers,
        bool Stopped);

    /// <summary>
    /// Runs the outbound preamble shared by the three request paths (SendRequestAsync,
    /// SendRequestMultiAsync, PublishRequestAsync): serialise the message (only when outgoing
    /// filters are registered), run the outgoing-filter pipeline if any, and stamp the headers.
    /// </summary>
    /// <remarks>
    /// Unlike <c>PrepareOutboundAsync</c> this helper does NOT always serialise: when no
    /// outgoing filters are configured the envelope is never built, so the local serialise
    /// can be skipped because <c>RequestReplyManager</c> re-serialises on the request leg.
    /// Two helpers (rather than one) preserve that optimisation.
    /// </remarks>
    internal async Task<RequestPreparation> PrepareOutboundForRequestAsync<T>(
        T message,
        IReadOnlyDictionary<string, string>? callerHeaders,
        CancellationToken cancellationToken) where T : Message
    {
        if (_hasOutgoingFilters)
        {
            // Serialize here only because outgoing filters need to inspect the wire body.
            // RequestReplyManager will serialize again on its own path; the duplicate cost
            // is confined to this branch.
            var bufferWriter = new System.Buffers.ArrayBufferWriter<byte>();
            _serializer.Serialize(message, bufferWriter);
            var messageBytes = bufferWriter.WrittenMemory;
            var envelope = CreateEnvelope(messageBytes, message.CorrelationId, typeof(T), callerHeaders);
            if (await RunOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false) == FilterAction.Stop)
            {
                return new RequestPreparation(null!, Stopped: true);
            }
            return new RequestPreparation(ExtractHeaders(envelope), Stopped: false);
        }

        return new RequestPreparation(BuildHeadersDirect(message.CorrelationId, callerHeaders), Stopped: false);
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
        // Capacity tracks ReservedHeaders.Count (currently 2: MessageId + CorrelationId) plus the
        // caller's headers. MessageType is not part of the reserved set, so this is already tight;
        // the dynamic count adjusts automatically if the set evolves.
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
