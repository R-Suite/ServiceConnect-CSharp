using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Services;

namespace ServiceConnect;

public sealed class Bus(
    IMessageSerializer serializer,
    IFilterPipeline filterPipeline,
    ISendMessagePipeline sendPipeline,
    IRequestReplyManager requestReplyManager,
    ILogger<Bus> logger,
    IQueueConfiguration queueConfig,
    IMessageDispatcher dispatcher,
    IList<HandlerReference> handlerReferences,
    IConsumer? consumer = null,
    IProducer? producer = null) : IBus
{
    private readonly IMessageSerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IFilterPipeline _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
    private readonly ISendMessagePipeline _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
    private readonly IRequestReplyManager _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
    private readonly ILogger<Bus> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IQueueConfiguration _queueConfig = queueConfig ?? throw new ArgumentNullException(nameof(queueConfig));
    private readonly IMessageDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly IList<HandlerReference> _handlerReferences = handlerReferences ?? throw new ArgumentNullException(nameof(handlerReferences));
    private readonly IConsumer? _consumer = consumer;
    private readonly IProducer? _producer = producer;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleSemaphore = new(1, 1);
    private bool _consuming;
    private volatile bool _disposed;

    public bool IsConnected => _consuming;

    public async Task PublishAsync<T>(T message, PublishOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (options?.RoutingKey is not null)
            headers[HeaderKeys.RoutingKey] = options.RoutingKey;

        await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(T), messageBytes, headers).ConfigureAwait(false);
    }

    public async Task SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) where T : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (options?.EndPoints is { Count: > 0 } endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, endpoint).ConfigureAwait(false);
            }
        }
        else
        {
            await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, options?.EndPoint).ConfigureAwait(false);
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

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");

        var headers = ExtractHeaders(envelope);

        return await _requestReplyManager.SendRequestAsync<T, TReply>(
            messageBytes,
            headers,
            (type, bytes, hdrs, endpoint) => _sendPipeline.ExecuteSendMessagePipelineAsync(type, bytes, hdrs, endpoint),
            requestOptions).ConfigureAwait(false);
    }

    public async Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null, CancellationToken cancellationToken = default)
        where T : Message where TReply : Message
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var requestOptions = options ?? new RequestOptions();
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, requestOptions.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            throw new InvalidOperationException("Outgoing filters blocked the request message.");

        var headers = ExtractHeaders(envelope);

        return await _requestReplyManager.SendRequestMultiAsync<T, TReply>(
            messageBytes,
            headers,
            (type, bytes, hdrs, endpoint) => _sendPipeline.ExecuteSendMessagePipelineAsync(type, bytes, hdrs, endpoint),
            requestOptions).ConfigureAwait(false);
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

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (destinations.Count > 1)
        {
            headers[HeaderKeys.RoutingSlip] = string.Join(",", destinations.Skip(1));
        }

        await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, firstDestination).ConfigureAwait(false);
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

            _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
                _queueConfig.QueueName, messageTypeNames.Count);

            await localConsumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch).ConfigureAwait(false);

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
