namespace ServiceConnect.Interfaces;

/// <summary>
/// Processes incoming messages before they are dispatched to handlers.
/// </summary>
public interface IMessageProcessor
{
    /// <summary>
    /// When true, this processor can run before the message body is deserialized
    /// (the message parameter will be null). Pre-deserialization processors still run
    /// inside the before/after consuming filter gate, but they are intentionally not
    /// wrapped by <see cref="IMessageProcessingMiddleware"/> — that middleware's delegate
    /// signature requires a non-null <c>object message</c>, which pre-deserialization
    /// processors by definition do not yet have.
    /// </summary>
    bool RunBeforeDeserialization => false;

    /// <summary>
    /// Processes an incoming message envelope.
    /// </summary>
    /// <param name="messageBytes">The raw message payload.</param>
    /// <param name="messageType">The resolved CLR message type.</param>
    /// <param name="message">The deserialized message instance, or <see langword="null"/> for pre-deserialization processors.</param>
    /// <param name="headers">The message headers.</param>
    /// <param name="envelope">The message envelope.</param>
    /// <param name="cancellationToken">A token that cancels processing.</param>
    /// <returns>The processor result.</returns>
    Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes,
        Type messageType,
        object? message,
        IDictionary<string, object> headers,
        Envelope envelope,
        CancellationToken cancellationToken = default);
}
