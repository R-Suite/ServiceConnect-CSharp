using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class ConsumeContext(IBus bus, IDictionary<string, object> headers) : IConsumeContext
{
    public IBus Bus { get; } = bus;
    public IDictionary<string, object> Headers { get; } = headers;
    public CancellationToken CancellationToken { get; set; }

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

        var replyHeaders = headers ?? [];
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

        var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        await Bus.SendAsync(message, options, cancellationToken).ConfigureAwait(false);
    }
}
