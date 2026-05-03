using System.Threading;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Process-manager handler: correlates incoming messages of type
/// <typeparamref name="TMessage"/> to a persisted <typeparamref name="TData"/>
/// instance keyed on <see cref="IProcessManagerData.CorrelationId"/>.
/// </summary>
/// <typeparam name="TData">Persisted state carried across messages in the saga.</typeparam>
/// <typeparam name="TMessage">Message contract routed into this handler.</typeparam>
public interface IProcessHandler<TData, TMessage>
    where TData : class, IProcessManagerData, new()
    where TMessage : Message
{
    /// <summary>
    /// Invoked with the deserialized message, the correlated persisted state, and the
    /// per-message consume context. Mutations to <paramref name="data"/> are persisted
    /// when the method returns. The <paramref name="cancellationToken"/> is sourced
    /// from the transport consume context and signals cooperative shutdown.
    /// </summary>
    /// <remarks>
    /// v8: <c>Context</c> moved from a property to a parameter (same rationale as
    /// <see cref="IMessageHandler{TMessage}.HandleAsync"/>). Migration: add
    /// <c>IConsumeContext context</c> between <paramref name="data"/> and
    /// <paramref name="cancellationToken"/>, and replace <c>this.Context</c> reads
    /// with <c>context</c>.
    /// </remarks>
    Task HandleAsync(TMessage message, TData data, IConsumeContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the correlation mapping between <typeparamref name="TMessage"/> and
    /// <typeparamref name="TData"/>. The default implementation maps on CorrelationId.
    /// </summary>
    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
