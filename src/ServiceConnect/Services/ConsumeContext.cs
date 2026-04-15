using System.Collections.ObjectModel;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class ConsumeContext(
    IBus bus,
    IDictionary<string, object> headers,
    IQueueConfiguration queueConfig,
    IBusConfiguration busConfig,
    CancellationToken cancellationToken = default) : IConsumeContext
{
    public IBus Bus { get; } = bus;

    /// <summary>
    /// Read-only view exposed to user handlers (R-088). The transport layer retains the
    /// mutable <see cref="IDictionary{TKey,TValue}"/> and continues to write pipeline
    /// headers (TimeProcessed, DestinationAddress, etc.) via that reference.
    /// </summary>
    public IReadOnlyDictionary<string, object> Headers { get; } = new ReadOnlyDictionary<string, object>(
        headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers));
    public CancellationToken CancellationToken { get; } = cancellationToken;

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

    public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message
    {
        var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? HeaderDecoder.Decode(sa) : null;
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? HeaderDecoder.Decode(rmi) : null;
        var isRequestReply = !string.IsNullOrEmpty(requestMessageId);

        if (busConfig.ValidateReplyDestinations && !isRequestReply && !IsKnownQueue(sourceAddress, queueConfig))
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
        // Check own queues
        if (string.Equals(address, queueConfig.QueueName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(address, queueConfig.ErrorQueueName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(address, queueConfig.AuditQueueName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Check all queue mapping values
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
}
