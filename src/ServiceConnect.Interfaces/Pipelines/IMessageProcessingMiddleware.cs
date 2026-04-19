namespace ServiceConnect.Interfaces;

/// <summary>
/// Represents the next step in the inbound message-processing middleware chain.
/// </summary>
/// <param name="messageBytes">The raw message payload.</param>
/// <param name="messageType">The resolved CLR message type.</param>
/// <param name="message">The deserialized message instance.</param>
/// <param name="headers">The message headers.</param>
/// <param name="envelope">The current message envelope.</param>
/// <param name="cancellationToken">A token that cancels processing.</param>
/// <returns>The consume result.</returns>
public delegate Task<ConsumeEventResult> MessageProcessingDelegate(
    ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
    IDictionary<string, object> headers, Envelope envelope,
    CancellationToken cancellationToken);

/// <summary>
/// Middleware that wraps inbound message processing.
/// </summary>
public interface IMessageProcessingMiddleware
{
    /// <summary>
    /// Processes an inbound message and optionally delegates to the next middleware.
    /// </summary>
    /// <param name="messageBytes">The raw message payload.</param>
    /// <param name="messageType">The resolved CLR message type.</param>
    /// <param name="message">The deserialized message instance.</param>
    /// <param name="headers">The message headers.</param>
    /// <param name="envelope">The current message envelope.</param>
    /// <param name="next">The next delegate in the chain.</param>
    /// <param name="cancellationToken">A token that cancels processing.</param>
    /// <returns>The consume result.</returns>
    Task<ConsumeEventResult> Process(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object message,
        IDictionary<string, object> headers, Envelope envelope,
        MessageProcessingDelegate next,
        CancellationToken cancellationToken);
}
