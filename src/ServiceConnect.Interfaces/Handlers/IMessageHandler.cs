namespace ServiceConnect.Interfaces;

/// <summary>
/// Implemented by classes that handle a specific message type. Multiple implementations
/// of <see cref="IMessageHandler{TMessage}"/> for the same message type may be registered
/// and will all be invoked in turn.
/// </summary>
/// <typeparam name="TMessage">The message contract handled by this implementation.</typeparam>
public interface IMessageHandler<in TMessage> where TMessage : Message
{
    /// <summary>
    /// The per-message consume context (bus handle, correlation id, reply helper).
    /// Populated by the dispatch pipeline before <see cref="HandleAsync"/> is called,
    /// so implementations may treat it as non-null. Initialise with
    /// <c>= null!;</c> to satisfy the nullable-reference-type analyser.
    /// </summary>
    IConsumeContext Context { get; set; }

    /// <summary>Invoked with the deserialized message.</summary>
    Task HandleAsync(TMessage message);
}
