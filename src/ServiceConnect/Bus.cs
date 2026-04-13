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
    private readonly Services.Processors.ProcessManagerHandlerRegistry _processManagerRegistry;
    private readonly IConsumer? _consumer;
    private readonly IProducer? _producer;
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
        Services.Processors.ProcessManagerHandlerRegistry processManagerRegistry,
        IConsumer? consumer = null,
        IProducer? producer = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
        _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueConfig = queueConfig ?? throw new ArgumentNullException(nameof(queueConfig));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handlerReferences = handlerReferences ?? throw new ArgumentNullException(nameof(handlerReferences));
        _processManagerRegistry = processManagerRegistry ?? throw new ArgumentNullException(nameof(processManagerRegistry));
        _consumer = consumer;
        _producer = producer;
    }

    public bool IsConnected => _consuming;

    public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            return;

        var headers = ExtractHeaders(envelope);

        if (options?.RoutingKey is not null)
            headers[HeaderKeys.RoutingKey] = options.RoutingKey;

        await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(T), messageBytes, headers, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            return;

        var headers = ExtractHeaders(envelope);

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
        var requestOptions = options ?? new RequestOptions();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);

        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");

        var headers = ExtractHeaders(envelope);

        return await _requestReplyManager.SendRequestAsync<T, TReply>(
            messageBytes,
            headers,
            _sendPipeline.ExecuteSendMessagePipelineAsync,
            requestOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? new RequestOptions();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);

        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");

        var headers = ExtractHeaders(envelope);

        return await _requestReplyManager.SendRequestMultiAsync<T, TReply>(
            messageBytes,
            headers,
            _sendPipeline.ExecuteSendMessagePipelineAsync,
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
        var envelope = CreateEnvelope(typeof(T), messageBytes);

        if (await _filterPipeline.ExecuteOutgoingFiltersAsync(envelope, cancellationToken).ConfigureAwait(false))
            return;

        var headers = ExtractHeaders(envelope);

        if (destinations.Count > 1)
        {
            headers[HeaderKeys.RoutingSlip] = string.Join(",", destinations.Skip(1));
        }

        await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, firstDestination, cancellationToken).ConfigureAwait(false);
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        ThrowIfDisposed();
        if (_producer == null)
            throw new InvalidOperationException("No producer registered. Cannot create stream.");
        return new MessageBusWriteStream(_producer, endpoint, typeof(T));
    }

    public async Task StartConsumingAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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

            _ = _processManagerRegistry; // touch singleton; duplicate-handler registration would have thrown at DI resolution time
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
                await localConsumer.DisposeAsync().ConfigureAwait(false);
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
        var headers = new Dictionary<string, string>();
        foreach (var kvp in envelope.Headers)
        {
            headers[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
        }
        return headers;
    }
}
