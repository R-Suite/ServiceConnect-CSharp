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
    /// <para>
    /// <b>Idempotency invariant.</b> The handler MUST be safe to invoke more than once
    /// for the same logical message. ServiceConnect delivers at-least-once: a transport
    /// redelivery (consumer crash before ack, broker requeue, optimistic-concurrency
    /// retry on <see cref="Exceptions.ConcurrencyException"/>) can replay <em>any</em>
    /// message into this handler, including after the handler has already mutated
    /// <paramref name="data"/> and committed the persistence write but the broker
    /// ack failed. Side effects with external observability — outbound bus sends,
    /// HTTP calls, DB writes outside the saga, file I/O — must therefore be guarded
    /// by an idempotency check (e.g., a state flag in <paramref name="data"/>, an
    /// IdempotencyKey on the outbound message, an upsert with a deterministic key).
    /// A handler that unconditionally <c>SendAsync</c>s an outbound command on every
    /// invocation will double-send on retry; that is the framework's contract, not
    /// a bug.
    /// </para>
    /// </remarks>
    Task HandleAsync(TMessage message, TData data, IConsumeContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the correlation mapping between <typeparamref name="TMessage"/> and
    /// <typeparamref name="TData"/>. The default implementation maps on CorrelationId.
    /// </summary>
    /// <remarks>
    /// <para><strong>Purity contract.</strong> The framework calls <see cref="ConfigureMapper"/>
    /// once per delivery and reuses the same mapper instance for both the initial saga lookup
    /// and the post-handler persistence find. Implementations MUST be pure with respect to
    /// handler-instance state — read from the <paramref name="mapper"/> parameter and the type
    /// system, not from mutable handler fields. Returning a mapping that depends on mutable state
    /// would cause inconsistent behaviour if the framework's call site is ever extended to call
    /// the method again.</para>
    /// </remarks>
    void ConfigureMapper(IProcessManagerPropertyMapper mapper)
    {
        mapper.ConfigureMapping<TData, TMessage>(d => d.CorrelationId, m => m.CorrelationId);
    }
}
