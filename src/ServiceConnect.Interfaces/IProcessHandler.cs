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
    /// The per-message consume context (bus handle, correlation id, reply helper).
    /// Populated by the dispatch pipeline before <see cref="HandleAsync"/> is called.
    /// </summary>
    IConsumeContext? Context { get; set; }

    /// <summary>
    /// Invoked with the deserialized message and the correlated persisted state.
    /// Mutations to <paramref name="data"/> are persisted when the method returns.
    /// </summary>
    Task HandleAsync(TMessage message, TData data);

    /// <summary>
    /// Configures the correlation mapping between <typeparamref name="TMessage"/> and
    /// <typeparamref name="TData"/>. The default implementation maps on CorrelationId.
    /// </summary>
    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
