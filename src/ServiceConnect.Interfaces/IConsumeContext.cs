namespace ServiceConnect.Interfaces;

/// <summary>
/// Per-message consume context supplied to handlers. Exposes the bus handle, raw
/// headers, correlation id, a per-message cancellation token, and a reply helper.
/// </summary>
public interface IConsumeContext
{
    /// <summary>The bus instance on which the message arrived.</summary>
    IBus Bus { get; }

    /// <summary>Raw headers as received from the transport (values may be byte[] or string).</summary>
    IDictionary<string, object> Headers { get; }

    /// <summary>Message id header, if present.</summary>
    string? MessageId { get; }

    /// <summary>Correlation id carried by the incoming message.</summary>
    Guid CorrelationId { get; }

    /// <summary>Cancellation token tied to the consumer loop; fires when consumption stops.</summary>
    CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Sends <paramref name="message"/> back to the requester as a reply, setting the
    /// <c>ResponseMessageId</c> header so the request/reply manager correlates it to the
    /// originating <c>SendRequestAsync</c> call.
    /// </summary>
    Task ReplyAsync<TReply>(TReply message, Dictionary<string, string>? headers = null, CancellationToken cancellationToken = default) where TReply : Message;
}
