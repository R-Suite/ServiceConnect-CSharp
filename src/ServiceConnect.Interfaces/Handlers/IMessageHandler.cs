using System.Threading;

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
    /// Invoked with the deserialized message and the per-message consume context
    /// (bus handle, correlation id, reply helper). The <paramref name="cancellationToken"/>
    /// is sourced from the transport consume context and signals cooperative shutdown.
    /// </summary>
    /// <remarks>
    /// The consume context is passed as a method parameter rather than a property so that a
    /// singleton-registered handler dispatched concurrently for two messages does not have
    /// one invocation's context overwritten by the other.
    /// </remarks>
    Task HandleAsync(TMessage message, IConsumeContext context, CancellationToken cancellationToken = default);
}
