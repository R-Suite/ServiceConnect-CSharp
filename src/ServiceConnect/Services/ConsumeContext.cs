using System.Text;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public class ConsumeContext : IConsumeContext
{
    public IBus Bus { get; set; }
    public IDictionary<string, object> Headers { get; set; }

    public string? MessageId =>
        Headers.TryGetValue(HeaderKeys.MessageId, out var value) ? DecodeHeaderValue(value) : null;

    public Guid CorrelationId =>
        Headers.TryGetValue(HeaderKeys.CorrelationId, out var value) && Guid.TryParse(DecodeHeaderValue(value), out var id)
            ? id : Guid.Empty;

    public ConsumeContext(IBus bus, IDictionary<string, object> headers)
    {
        Bus = bus;
        Headers = headers;
    }

    public async Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null) where TReply : Message
    {
        var sourceAddress = Headers.TryGetValue(HeaderKeys.SourceAddress, out var sa) ? DecodeHeaderValue(sa) : null;
        if (string.IsNullOrEmpty(sourceAddress))
            throw new InvalidOperationException("Cannot reply: incoming message has no SourceAddress header.");

        var requestMessageId = Headers.TryGetValue(HeaderKeys.RequestMessageId, out var rmi) ? DecodeHeaderValue(rmi) : null;

        var replyHeaders = headers ?? new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(requestMessageId))
            replyHeaders[HeaderKeys.ResponseMessageId] = requestMessageId;

        var options = new SendOptions { EndPoint = sourceAddress, Headers = replyHeaders };
        await Bus.SendAsync(message, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes a header value that may be a byte array (as returned by RabbitMQ) or already a string.
    /// </summary>
    private static string? DecodeHeaderValue(object? value)
    {
        if (value is byte[] bytes)
            return Encoding.UTF8.GetString(bytes);
        return value?.ToString();
    }
}
