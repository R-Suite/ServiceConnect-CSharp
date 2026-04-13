namespace ServiceConnect.Interfaces;

/// <summary>
/// Handler for byte-stream messages: large payloads are delivered as a sequence of
/// packets reassembled into <see cref="Stream"/>, and <see cref="Execute"/> is called
/// once the complete stream has arrived.
/// </summary>
/// <typeparam name="TMessage">Message contract associated with the stream.</typeparam>
public interface IStreamHandler<TMessage> where TMessage : Message
{
    /// <summary>
    /// The read stream from which to retrieve the assembled payload bytes.
    /// Populated by the dispatch pipeline before <see cref="Execute"/> is called.
    /// </summary>
    IMessageBusReadStream Stream { get; set; }

    /// <summary>Invoked once the full stream has been received and reassembled.</summary>
    void Execute(TMessage stream);
}
