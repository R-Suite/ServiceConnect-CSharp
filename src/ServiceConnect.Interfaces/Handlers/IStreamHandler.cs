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
    /// <remarks>v8: <c>Stream</c> moved from a property to this parameter (analogous to
    /// the <c>Context</c> change on <see cref="IMessageHandler{TMessage}"/>).</remarks>
    Task ExecuteAsync(TMessage message, IMessageBusReadStream stream, CancellationToken cancellationToken = default);
}
