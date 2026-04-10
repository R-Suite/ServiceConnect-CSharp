using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect;

public sealed class Bus : IBus
{
    private readonly IMessageSerializer _serializer;
    private readonly IFilterPipeline _filterPipeline;
    private readonly ISendMessagePipeline _sendPipeline;
    private readonly IRequestReplyManager _requestReplyManager;
    private readonly IBusConfiguration _config;
    private readonly ILogger<Bus> _logger;
    private bool _consuming;
    private bool _disposed;

    public Bus(
        IMessageSerializer serializer,
        IFilterPipeline filterPipeline,
        ISendMessagePipeline sendPipeline,
        IRequestReplyManager requestReplyManager,
        IBusConfiguration config,
        ILogger<Bus> logger)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _filterPipeline = filterPipeline ?? throw new ArgumentNullException(nameof(filterPipeline));
        _sendPipeline = sendPipeline ?? throw new ArgumentNullException(nameof(sendPipeline));
        _requestReplyManager = requestReplyManager ?? throw new ArgumentNullException(nameof(requestReplyManager));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    public void Route<T>(T message, IList<string> destinations) where T : Message
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

        Task.Run(() => _sendPipeline.ExecuteSendMessagePipelineAsync(typeof(T), messageBytes, headers, firstDestination))
            .GetAwaiter()
            .GetResult();
    }

    public IMessageBusWriteStream CreateStream<T>(string endpoint, T message) where T : Message
    {
        throw new NotImplementedException("Stream support will be wired in a later task.");
    }

    public void StartConsuming()
    {
        _logger.LogInformation("Bus starting to consume messages.");
        _consuming = true;
    }

    public Task StartConsumingAsync()
    {
        StartConsuming();
        return Task.CompletedTask;
    }

    public void StopConsuming()
    {
        _logger.LogInformation("Bus stopping message consumption.");
        _consuming = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
