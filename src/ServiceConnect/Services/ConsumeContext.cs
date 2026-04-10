using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class ConsumeContext : IConsumeContext
{
    public IBus Bus { get; set; }
    public IDictionary<string, object> Headers { get; set; }

    public string? MessageId =>
        Headers.TryGetValue(HeaderKeys.MessageId, out var value) ? value?.ToString() : null;

    public Guid CorrelationId =>
        Headers.TryGetValue(HeaderKeys.CorrelationId, out var value) && Guid.TryParse(value?.ToString(), out var id)
            ? id : Guid.Empty;

    public ConsumeContext(IBus bus, IDictionary<string, object> headers)
    {
        Bus = bus;
        Headers = headers;
    }

    public void Reply<TReply>(TReply message, Dictionary<string, string>? headers = null) where TReply : Message
    {
        var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? sa?.ToString() : null;
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? rmi?.ToString() : null;

        var replyHeaders = headers ?? new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders["ResponseMessageId"] = requestMessageId;

        var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        Bus.SendAsync(message, options).GetAwaiter().GetResult();
    }
}
