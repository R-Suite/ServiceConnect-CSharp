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
    /// v8: <c>Context</c> moved from a property to this parameter. Pre-v8 the framework
    /// assigned <c>handler.Context</c> before calling <c>HandleAsync(message, ct)</c>; that
    /// shape was unsafe for singleton-registered handlers (concurrent dispatches both
    /// wrote the property). Migration: append <c>IConsumeContext context</c> to the method
    /// signature and replace <c>this.Context</c> reads with <c>context</c>.
    /// </remarks>
    Task HandleAsync(TMessage message, IConsumeContext context, CancellationToken cancellationToken = default);
}
