using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Coordinates request/reply interactions on top of the transport pipeline.
/// </summary>
public interface IRequestReplyManager
{
    /// <summary>
    /// Sends a request and waits for a single reply.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TReply">The expected reply type.</typeparam>
    /// <param name="message">The request message.</param>
    /// <param name="headers">The outgoing headers to send with the request.</param>
    /// <param name="options">Request routing and timeout options.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The deserialized reply.</returns>
    Task<TReply> SendRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    /// <summary>
    /// Sends a request and collects multiple replies.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TReply">The expected reply type.</typeparam>
    /// <param name="message">The request message.</param>
    /// <param name="headers">The outgoing headers to send with the request.</param>
    /// <param name="options">Request routing and timeout options.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The replies collected before completion.</returns>
    Task<IList<TReply>> SendRequestMultiAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        RequestOptions options,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    /// <summary>
    /// Publishes a request and invokes a callback for each reply that arrives.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TReply">The expected reply type.</typeparam>
    /// <param name="message">The request message.</param>
    /// <param name="headers">The outgoing headers to send with the request.</param>
    /// <param name="options">Request routing and timeout options.</param>
    /// <param name="onReply">The callback to invoke for each reply.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    Task PublishRequestAsync<TRequest, TReply>(
        TRequest message,
        IDictionary<string, string> headers,
        RequestOptions options,
        Action<TReply> onReply,
        CancellationToken cancellationToken = default)
        where TRequest : Message
        where TReply : Message;

    /// <summary>
    /// Attempts to match an incoming reply to a pending request.
    /// </summary>
    /// <param name="messageId">The correlation identifier used to track the pending request.</param>
    /// <param name="messageBytes">The serialized reply payload.</param>
    /// <param name="type">The CLR type of the reply message.</param>
    void ProcessReply(string messageId, ReadOnlyMemory<byte> messageBytes, Type type);
}
