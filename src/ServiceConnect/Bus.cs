using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;

namespace ServiceConnect;

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
    private readonly bool _hasOutgoingFilters;
    private readonly TimeSpan _disposeTimeout;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleSemaphore = new(1, 1);
    private bool _consuming;
    private volatile bool _disposed;

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
        IConsumer? consumer = null,
        IProducer? producer = null,
        TimeSpan? disposeTimeout = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
        _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueConfig = queueConfig ?? throw new ArgumentNullException(nameof(queueConfig));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handlerReferences = handlerReferences ?? throw new ArgumentNullException(nameof(handlerReferences));
        if (pipelineConfig == null) throw new ArgumentNullException(nameof(pipelineConfig));
        _hasOutgoingFilters = pipelineConfig.OutgoingFilters.Count > 0;
        _consumer = consumer;
        _producer = producer;
        _disposeTimeout = disposeTimeout ?? TimeSpan.FromSeconds(30);
    }

    public bool IsConsuming => _consuming;

    public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);
            if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
                return;
            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(typeof(T), options?.Headers);
        }

        if (options?.RoutingKey is not null)
            headers[HeaderKeys.RoutingKey] = options.RoutingKey;

        await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(T), messageBytes, headers, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);
            if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
                return;
            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(typeof(T), options?.Headers);
        }

        if (options?.EndPoints is { Count: > 0 } endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, endpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, options?.EndPoint, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);
            if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Outgoing filters blocked the request message.");
            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(typeof(T), requestOptions.Headers);
        }

        return await _requestReplyManager.SendRequestAsync<T, TReply>(
            messageBytes,
            headers,
            requestOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? RequestOptions.Default;
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);
            if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Outgoing filters blocked the request message.");
            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(typeof(T), requestOptions.Headers);
        }

        return await _requestReplyManager.SendRequestMultiAsync<T, TReply>(
            messageBytes,
            headers,
            requestOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where TRequest : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var replies = await SendRequestMultiAsync<TRequest, TReply>(message, options, cancellationToken).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            onReply(reply);
        }
    }

    public async Task RouteAsync<T>(T message, IList<string> destinations, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (destinations == null || destinations.Count == 0)
            throw new ArgumentException("At least one destination is required.", nameof(destinations));

        var firstDestination = destinations[0];
        var messageBytes = _serializer.Serialize(message);
        Dictionary<string, string> headers;

        if (_hasOutgoingFilters)
        {
            var envelope = CreateEnvelope(typeof(T), messageBytes);
            if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
                return;
            headers = ExtractHeaders(envelope);
        }
        else
        {
            headers = BuildHeadersDirect(typeof(T), null);
        }

        if (destinations.Count > 1)
        {
            headers[HeaderKeys.RoutingSlip] = BuildRoutingSlip(destinations);
        }

        await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, firstDestination, cancellationToken).ConfigureAwait(false);
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint) where T : Message
    {
        ThrowIfDisposed();
        if (_producer == null)
            throw new InvalidOperationException("No producer registered. Cannot create stream.");
        return new MessageBusWriteStream(_producer, endpoint, typeof(T));
    }

    public async Task StartConsumingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(_queueConfig.QueueName))
            throw new InvalidOperationException(
                "QueueName is not set. Configure via ServiceConnectBuilder.ConfigureQueues(q => q.QueueName = \"...\") before starting consumption (E-07).");

        await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IConsumer localConsumer;
            List<string> messageTypeNames;

            lock (_stateLock)
            {
                if (_consumer == null)
                    throw new InvalidOperationException("No consumer registered. Call UseRabbitMQ() or register an IConsumer.");

                messageTypeNames =
                [
                    .. _handlerReferences
                        .Select(h => h.MessageType.FullName!.Replace(".", string.Empty))
                        .Distinct()
                ];

                localConsumer = _consumer;
            }

            _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
                _queueConfig.QueueName, messageTypeNames.Count);

            await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch, cancellationToken).ConfigureAwait(false);

            lock (_stateLock) { _consuming = true; }
        }
        finally
        {
            _lifecycleSemaphore.Release();
        }
    }

    public async Task StopConsumingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await StopConsumingCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops consuming without the disposed guard. Called from DisposeAsync,
    /// which sets _disposed = true before invoking this -- a ThrowIfDisposed()
    /// here would throw ObjectDisposedException and prevent clean shutdown.
    /// Public callers must use StopConsumingAsync instead, which adds the guard.
    /// </summary>
    private async Task StopConsumingCoreAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IConsumer? localConsumer = null;
            lock (_stateLock)
            {
                _logger.LogInformation("Bus stopping message consumption.");
                if (_consuming)
                {
                    _consuming = false;
                    localConsumer = _consumer;
                }
            }
            if (localConsumer != null)
            {
                try
                {
                    await localConsumer.DisposeAsync().AsTask().WaitAsync(_disposeTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Timed out waiting {Timeout} for consumer disposal.", _disposeTimeout);
                }
            }
        }
        finally
        {
            _lifecycleSemaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await StopConsumingCoreAsync().ConfigureAwait(false);
        _sendPipeline.Dispose();
        if (_producer != null)
            await _producer.DisposeAsync().ConfigureAwait(false);
        _lifecycleSemaphore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static Envelope CreateEnvelope(Type messageType, byte[] body, Dictionary<string, string>? additionalHeaders = null)
    {
        var envelope = new Envelope
        {
            Body = body,
            Headers = new Dictionary<string, object>
            {
                [HeaderKeys.MessageType] = messageType.FullName ?? messageType.Name
            }
        };

        if (additionalHeaders is not null)
        {
            foreach (var header in additionalHeaders)
            {
                envelope.Headers[header.Key] = header.Value;
            }
        }

        return envelope;
    }

    private static Dictionary<string, string> ExtractHeaders(Envelope envelope)
    {
        // Pre-size the destination to the known envelope header count so the
        // dictionary is not rehashed as we fill it (P-03).
        var headers = new Dictionary<string, string>(envelope.Headers.Count);
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
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < destinations.Count; index++)
        {
            if (index > 1)
                builder.Append(',');

            builder.Append(destinations[index]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Fast-path header builder used when no outgoing filters are registered (P-009).
    /// Produces the same <see cref="Dictionary{TKey,TValue}"/> that
    /// <see cref="CreateEnvelope"/> + <see cref="ExtractHeaders"/> would return,
    /// without allocating the intermediate <see cref="Envelope"/> or its
    /// <c>Dictionary&lt;string, object&gt;</c> headers map.
    /// </summary>
    private static Dictionary<string, string> BuildHeadersDirect(Type messageType, Dictionary<string, string>? additionalHeaders)
    {
        var capacity = 1 + (additionalHeaders?.Count ?? 0);
        var headers = new Dictionary<string, string>(capacity)
        {
            [HeaderKeys.MessageType] = messageType.FullName ?? messageType.Name
        };

        if (additionalHeaders is not null)
        {
            foreach (var kvp in additionalHeaders)
                headers[kvp.Key] = kvp.Value;
        }

        return headers;
    }
}
