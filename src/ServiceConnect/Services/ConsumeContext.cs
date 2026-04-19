using System.Collections.ObjectModel;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class ConsumeContext : IConsumeContext
{
    private readonly IQueueConfiguration _queueConfig;
    private readonly IBusConfiguration _busConfig;
    private readonly IReplyStatusRequestReplyManager? _replyStatusRequestReplyManager;

    public ConsumeContext(
        IBus bus,
        IDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IBusConfiguration busConfig,
        CancellationToken cancellationToken = default)
        : this(bus, headers, queueConfig, busConfig, null, cancellationToken)
    {
    }

    internal ConsumeContext(
        IBus bus,
        IDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IBusConfiguration busConfig,
        IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
        CancellationToken cancellationToken = default)
    {
        Bus = bus;
        _queueConfig = queueConfig;
        _busConfig = busConfig;
        _replyStatusRequestReplyManager = replyStatusRequestReplyManager;
        Headers = new ReadOnlyDictionary<string, object>(
            headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers));
        CancellationToken = cancellationToken;
    }

    public IBus Bus { get; }

    /// <summary>
    /// Read-only view exposed to user handlers (R-088). The transport layer retains the
    /// mutable <see cref="IDictionary{TKey,TValue}"/> and continues to write pipeline
    /// headers (TimeProcessed, DestinationAddress, etc.) via that reference.
    /// </summary>
    public IReadOnlyDictionary<string, object> Headers { get; }
    public CancellationToken CancellationToken { get; }

    // Cached backing fields — HeaderDecoder.Decode + Guid.TryParse are called only once
    // per ConsumeContext instance regardless of how many times the properties are read (P-031).
    private string? _messageId;
    private bool _messageIdCached;
    private Guid? _correlationId;

    public string? MessageId
    {
        get
        {
            if (!_messageIdCached)
            {
                _messageId = Headers.TryGetValue(HeaderKeys.MessageId, out var value)
                    ? HeaderDecoder.Decode(value) : null;
                _messageIdCached = true;
            }

            return _messageId;
        }
    }

    public Guid CorrelationId
    {
        get
        {
            if (_correlationId is null)
            {
                _correlationId = Headers.TryGetValue(HeaderKeys.CorrelationId, out var value)
                    && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
                    ? id : Guid.Empty;
            }

            return _correlationId.Value;
        }
    }

    public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
        where TReply : Message
    {
        var sourceAddress = GetDecodedHeader(Headers, HeaderKeys.SourceAddress);
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = GetDecodedHeader(Headers, HeaderKeys.RequestMessageId);
        var isTrustedRequestReply = IsTrustedRequestReplyEnvelope(Headers, _queueConfig, _replyStatusRequestReplyManager, requestMessageId, sourceAddress);

        if (_busConfig.ValidateReplyDestinations && !isTrustedRequestReply && !IsKnownQueue(sourceAddress, _queueConfig))
        {
            throw new InvalidOperationException(
                $"Cannot reply: SourceAddress '{sourceAddress}' is not a recognized queue. " +
                "This may indicate a spoofed message. Configure queue mappings or use RequestReplyManager for safe replies.");
        }

        var replyHeaders = headers ?? [];
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

        var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        await Bus.SendAsync(message, options, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsKnownQueue(string address, IQueueConfiguration queueConfig)
    {
        if (string.Equals(address, queueConfig.QueueName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(address, queueConfig.ErrorQueueName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(address, queueConfig.AuditQueueName, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var kvp in queueConfig.QueueMappings)
        {
            foreach (var queue in kvp.Value)
            {
                if (string.Equals(address, queue, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    internal static bool IsTrustedRequestReplyEnvelope(
        IReadOnlyDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
        string? requestMessageId = null,
        string? sourceAddress = null)
    {
        requestMessageId ??= GetDecodedHeader(headers, HeaderKeys.RequestMessageId);
        if (string.IsNullOrEmpty(requestMessageId))
            return false;

        if (replyStatusRequestReplyManager?.IsTrackedRequest(requestMessageId) == true)
            return true;

        sourceAddress ??= GetDecodedHeader(headers, HeaderKeys.SourceAddress);
        var responseMessageId = GetDecodedHeader(headers, HeaderKeys.ResponseMessageId);
        var destinationAddress = GetDecodedHeader(headers, HeaderKeys.DestinationAddress);
        var messageId = GetDecodedHeader(headers, HeaderKeys.MessageId);

        return string.IsNullOrEmpty(responseMessageId)
            && !string.IsNullOrEmpty(sourceAddress)
            && !string.IsNullOrEmpty(messageId)
            && string.Equals(destinationAddress, queueConfig.QueueName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? GetDecodedHeader(IReadOnlyDictionary<string, object> headers, string key)
    {
        return headers.TryGetValue(key, out var value) ? HeaderDecoder.Decode(value) : null;
    }
}
