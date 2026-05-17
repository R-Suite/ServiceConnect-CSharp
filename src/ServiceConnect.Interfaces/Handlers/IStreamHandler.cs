namespace ServiceConnect.Interfaces;

/// <summary>
/// Handler for byte-stream messages: large payloads are delivered as a sequence of
/// packets reassembled into <see cref="IMessageBusReadStream"/>, and <see cref="ExecuteAsync"/> is called
/// once the complete stream has arrived.
/// </summary>
/// <typeparam name="TMessage">Message contract associated with the stream.</typeparam>
public interface IStreamHandler<TMessage> where TMessage : Message
{
    /// <summary>
    /// Invoked once the full stream has been received and reassembled. Reads the
    /// assembled payload bytes from <paramref name="stream"/>.
    /// </summary>
    /// <param name="message">The control message that initiated the stream.</param>
    /// <param name="stream">The reassembled byte stream.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    Task ExecuteAsync(TMessage message, IMessageBusReadStream stream, CancellationToken cancellationToken = default);
}
