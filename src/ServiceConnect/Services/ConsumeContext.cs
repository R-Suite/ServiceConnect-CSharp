using System.Collections.ObjectModel;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

/// <summary>
/// Default <see cref="IConsumeContext"/> implementation exposed to message handlers while a message is being processed.
/// </summary>
internal sealed class ConsumeContext : IConsumeContext
{
    private readonly IQueueConfiguration _queueConfig;
    private readonly IBusConfiguration _busConfig;
    private readonly IReplyStatusRequestReplyManager? _replyStatusRequestReplyManager;

    /// <summary>
    /// Creates a consume context for a handler invocation.
    /// </summary>
    /// <param name="bus">The bus instance that can be used for reply operations.</param>
    /// <param name="headers">The decoded message headers.</param>
    /// <param name="queueConfig">The configured queue settings.</param>
    /// <param name="busConfig">The configured bus settings.</param>
    /// <param name="cancellationToken">The cancellation token for the current consume operation.</param>
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
            headers as Dictionary<string, object> ?? new Dictionary<string, object>(headers, StringComparer.Ordinal));
        CancellationToken = cancellationToken;
    }

    /// <inheritdoc />
    public IBus Bus { get; }

    /// <summary>
    /// Read-only view exposed to user handlers. The transport layer retains the
    /// mutable <see cref="IDictionary{TKey,TValue}"/> and continues to write pipeline
    /// headers (TimeProcessed, DestinationAddress, etc.) via that reference.
    /// </summary>
    public IReadOnlyDictionary<string, object> Headers { get; }
    /// <inheritdoc />
    public CancellationToken CancellationToken { get; }

    // Cached backing fields — HeaderDecoder.Decode + Guid.TryParse are called only once
    // per ConsumeContext instance regardless of how many times the properties are read.
    //
    // Memory-model contract: each cached payload field is plain; the volatile bool flag
    // publishes it. Writers MUST write the payload before the flag; readers MUST check
    // the flag before reading the payload. The flag's release/acquire semantics
    // guarantee a non-torn payload read on every architecture (incl. weakly-ordered ARM).
    // A racing reader may run the resolution twice (idempotent — string compare /
    // Guid.TryParse on the same input), but never observes a torn write.
    private string? _messageId;
    private volatile bool _messageIdCached;
    private Guid _correlationIdValue;
    private volatile bool _correlationIdCached;

    /// <inheritdoc />
    public string? MessageId
    {
        get
        {
            if (_messageIdCached)
            {
                return _messageId;
            }

            var value = Headers.TryGetValue(HeaderKeys.MessageId, out var raw)
                ? HeaderDecoder.Decode(raw) : null;
            _messageId = value;
            _messageIdCached = true;  // volatile write — release barrier publishes _messageId
            return value;
        }
    }

    /// <inheritdoc />
    public Guid CorrelationId
    {
        get
        {
            if (_correlationIdCached)
            {
                return _correlationIdValue;
            }

            var value = Headers.TryGetValue(HeaderKeys.CorrelationId, out var raw)
                    && Guid.TryParse(HeaderDecoder.Decode(raw), out var id)
                ? id : Guid.Empty;
            _correlationIdValue = value;
            _correlationIdCached = true;  // volatile write — release barrier publishes _correlationIdValue
            return value;
        }
    }

    /// <inheritdoc />
    public async Task ReplyAsync<TReply>(TReply message, ReplyOptions? options = null, CancellationToken cancellationToken = default)
        where TReply : Message
    {
        var sourceAddress = GetDecodedHeader(Headers, HeaderKeys.SourceAddress);
        if (string.IsNullOrEmpty(sourceAddress))
        {
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");
        }

        var requestMessageId = GetDecodedHeader(Headers, HeaderKeys.RequestMessageId);
        var isTrustedRequestReply = IsTrustedRequestReplyEnvelope(Headers, _queueConfig, _replyStatusRequestReplyManager, _busConfig, requestMessageId, sourceAddress);

        if (_busConfig.ValidateReplyDestinations && !isTrustedRequestReply && !IsKnownQueue(sourceAddress, _queueConfig))
        {
            throw new InvalidOperationException(
                $"Cannot reply: SourceAddress '{sourceAddress}' is not a recognized queue. " +
                "This may indicate a spoofed message. Configure queue mappings or use RequestReplyManager for safe replies.");
        }

        var callerHeaders = options?.Headers;
        Dictionary<string, string> replyHeaders = callerHeaders is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(callerHeaders, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(requestMessageId))
        {
            replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;
        }

        var sendOptions = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        await Bus.SendAsync(message, sendOptions, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsKnownQueue(string address, IQueueConfiguration queueConfig)
    {
        if (string.Equals(address, queueConfig.QueueName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(address, queueConfig.ErrorQueueName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(address, queueConfig.AuditQueueName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var kvp in queueConfig.QueueMappings)
        {
            foreach (var queue in kvp.Value)
            {
                if (string.Equals(address, queue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether the inbound envelope should be trusted as a legitimate request
    /// the local bus may safely reply to, bypassing the
    /// <see cref="IBusConfiguration.ValidateReplyDestinations"/> queue allow-list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two paths grant trust:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Tracked request</b> — the local <see cref="IReplyStatusRequestReplyManager"/>
    ///     records the <c>RequestMessageId</c>, proving WE originated the request. This
    ///     path is always honoured.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Heuristic fallback</b> — the headers look like a request envelope (non-empty
    ///     <c>RequestMessageId</c>, no <c>ResponseMessageId</c>, non-empty
    ///     <c>SourceAddress</c> and <c>MessageId</c>, <c>DestinationAddress</c> equals our
    ///     queue). This preserves cross-bus request-reply where the originator is a
    ///     different bus instance whose <c>RequestMessageId</c> we cannot have tracked.
    ///     Every header here can be crafted by any external producer that knows our queue
    ///     name, so a hostile peer can redirect our reply by spoofing them. The fallback
    ///     is gated on <see cref="IBusConfiguration.StrictReplyValidation"/>: when that
    ///     flag is <c>true</c> the fallback is disabled and only the tracked-request path
    ///     above is trusted.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Default <see cref="IBusConfiguration.StrictReplyValidation"/> is <c>false</c>, so
    /// existing behaviour is preserved exactly. Set the flag to <c>true</c> to remove the
    /// crafted-envelope attack surface in deployments that don't depend on cross-bus
    /// request-reply.
    /// </para>
    /// </remarks>
    internal static bool IsTrustedRequestReplyEnvelope(
        IReadOnlyDictionary<string, object> headers,
        IQueueConfiguration queueConfig,
        IReplyStatusRequestReplyManager? replyStatusRequestReplyManager,
        IBusConfiguration busConfig,
        string? requestMessageId = null,
        string? sourceAddress = null)
    {
        requestMessageId ??= GetDecodedHeader(headers, HeaderKeys.RequestMessageId);
        if (string.IsNullOrEmpty(requestMessageId))
        {
            return false;
        }

        if (replyStatusRequestReplyManager?.IsTrackedRequest(requestMessageId) == true)
        {
            return true;
        }

        // Strict mode disables the heuristic fallback — only locally-tracked requests are
        // trusted. The fallback below relies on headers (RequestMessageId, SourceAddress,
        // MessageId, DestinationAddress) that can all be set by any external producer that
        // knows our queue name; trusting them is necessary for cross-bus request-reply but
        // exposes a redirect-our-reply attack surface. See IBusConfiguration.
        if (busConfig.StrictReplyValidation)
        {
            return false;
        }

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
