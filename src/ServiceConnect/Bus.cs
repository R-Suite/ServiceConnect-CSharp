using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

namespace ServiceConnect;

public sealed class Bus : IBus
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly ISendMessagePipeline _sendPipeline;
    private readonly IRequestReplyManager _requestReplyManager;
    private readonly IBusConfiguration _config;
    private readonly ILogger<Bus> _logger;
    private readonly IQueueConfiguration _queueConfig;
    private readonly MessageDispatcher _dispatcher;
    private readonly IList<HandlerReference> _handlerReferences;
    private readonly IProducer? _producer;
    private readonly object _stateLock = new();
    private IConsumer? _consumer;
    private bool _consuming;
    private bool _disposed;

    public Bus(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        ISendMessagePipeline sendPipeline,
        IRequestReplyManager requestReplyManager,
        IBusConfiguration config,
        ILogger<Bus> logger,
        IQueueConfiguration queueConfig,
        MessageDispatcher dispatcher,
        IList<HandlerReference> handlerReferences,
        IConsumer? consumer = null,
        IProducer? producer = null)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
        _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _queueConfig = queueConfig ?? throw new ArgumentNullException(nameof(queueConfig));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handlerReferences = handlerReferences ?? throw new ArgumentNullException(nameof(handlerReferences));
        _consumer = consumer;
        _producer = producer;
    }

    public bool IsConnected => _consuming;

    public async Task PublishAsync<T>(T message, PublishOptions? options = null) where T : Message
    {
        var messageBytes = _serializer.Serialize(message);
        var envelope = CreateEnvelope(typeof(T), messageBytes, options?.Headers);

        if (_filterPipeline.ExecuteOutgoingFilters(envelope))
            return;

        var headers = ExtractHeaders(envelope);

        if (options?.RoutingKey is not null)
            headers[HeaderKeys.RoutingKey] = options.RoutingKey;

        await _sendPipeline.ExecutePublishMessagePipelineAsync(typeof(T), messageBytes, headers).ConfigureAwait(false);
    }

    public async Task SendAsync<T>(T message, SendOptions? options = null) where T : Message
    {
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

    public async Task<TReply> SendRequestAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message
    {
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

    public async Task<IList<TReply>> SendRequestMultiAsync<T, TReply>(T message, RequestOptions? options = null)
        where T : Message where TReply : Message
    {
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

    public async Task PublishRequestAsync<TRequest, TReply>(TRequest message, Action<TReply> onReply, RequestOptions? options = null)
        where TRequest : Message where TReply : Message
    {
        var replies = await SendRequestMultiAsync<TRequest, TReply>(message, options).ConfigureAwait(false);

        foreach (var reply in replies)
        {
            onReply(reply);
        }
    }

    public async Task RouteAsync<T>(T message, IList<string> destinations) where T : Message
    {
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
            var remainingDestinations = new List<string>();
            for (var i = 1; i < destinations.Count; i++)
                remainingDestinations.Add(destinations[i]);

            headers[HeaderKeys.RoutingSlip] = string.Join(",", remainingDestinations);
        }

        await _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, firstDestination).ConfigureAwait(false);
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        if (_producer == null)
            throw new InvalidOperationException("No producer registered. Cannot create stream.");
        return new MessageBusWriteStream(_producer, endpoint, typeof(T));
    }

    public void StartConsuming()
    {
        StartConsumingAsync().GetAwaiter().GetResult();
    }

    public async Task StartConsumingAsync()
    {
        IConsumer consumer;
        List<string> messageTypeNames;

        lock (_stateLock)
        {
            if (_consumer == null)
                throw new InvalidOperationException("No consumer registered. Call UseRabbitMQ() or register an IConsumer.");

            messageTypeNames = _handlerReferences
                .Select(h => h.MessageType.FullName!.Replace(".", string.Empty))
                .Distinct()
                .ToList();

            consumer = _consumer;
        }

        _logger.LogInformation("Bus starting to consume on queue {QueueName} for {Count} message types.",
            _queueConfig.QueueName, messageTypeNames.Count);

        await consumer.StartConsumingAsync(_queueConfig.QueueName, messageTypeNames, _dispatcher.Dispatch);

        lock (_stateLock)
        {
            _consuming = true;
        }
    }

    public void StopConsuming()
    {
        lock (_stateLock)
        {
            _logger.LogInformation("Bus stopping message consumption.");
            if (_consuming)
            {
                _consuming = false;
                _consumer?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        StopConsuming();
        _sendPipeline.Dispose();
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
