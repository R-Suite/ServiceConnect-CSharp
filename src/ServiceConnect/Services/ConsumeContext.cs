using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class ConsumeContext(IBus bus, IDictionary<string, object> headers) : IConsumeContext
{
    public IBus Bus { get; } = bus;
    public IDictionary<string, object> Headers { get; } = headers;

    public string? MessageId =>
        Headers.TryGetValue(HeaderKeys.MessageId, out var value) ? HeaderDecoder.Decode(value) : null;

    public Guid CorrelationId =>
        Headers.TryGetValue(HeaderKeys.CorrelationId, out var value) && Guid.TryParse(HeaderDecoder.Decode(value), out var id)
            ? id : Guid.Empty;

    public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null) where TReply : Message
    {
        var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? HeaderDecoder.Decode(sa) : null;
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? HeaderDecoder.Decode(rmi) : null;

        var replyHeaders = headers ?? [];
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

        var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        await Bus.SendAsync(message, options).ConfigureAwait(false);
    }
}
