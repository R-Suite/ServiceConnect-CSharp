using System.Linq.Expressions;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Defines how process-manager properties are matched against incoming message properties.
/// </summary>
public interface IProcessManagerPropertyMapper
{
    /// <summary>
    /// Gets the configured process-manager to message mappings.
    /// </summary>
    IReadOnlyList<ProcessManagerToMessageMap> Mappings { get; }

    /// <summary>
    /// Adds a mapping between a process-manager property and a message property.
    /// </summary>
    /// <typeparam name="TProcessManagerData">The process-manager data type.</typeparam>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="processManagerProperty">The process-manager property selector.</param>
    /// <param name="messageExpression">The message property selector.</param>
    /// <remarks>
    /// <para>
    /// <b>Cross-process duplicate-saga warning.</b> When this method is used to correlate on a
    /// property other than <c>CorrelationId</c> (e.g. <c>OrderNumber</c>), the storage layer's
    /// uniqueness fence is the <c>CorrelationId</c> column ONLY. Two cluster nodes can each
    /// process a "start" message for the same business key simultaneously, each generate a
    /// fresh <c>CorrelationId</c> for the new saga, and BOTH inserts will succeed — fragmenting
    /// the saga's state across two rows. The in-process correlation lock fences this within a
    /// single node but not across nodes. If you run clustered consumers and rely on a
    /// custom-mapped property for correlation, the user code MUST enforce uniqueness on that
    /// property at the storage layer (e.g. a Mongo unique index created out-of-band on
    /// <c>Data.&lt;YourProperty&gt;</c>, or an idempotency-key pattern at the message-source
    /// side that ensures only one node receives the start).
    /// </para>
    /// <para>
    /// Calling <see cref="ConfigureMapping"/> more than once for the same <typeparamref name="TMessage"/>
    /// is rejected — the first registration wins via the framework's lookup-by-FirstOrDefault.
    /// Implementations should throw on duplicate <typeparamref name="TMessage"/> rather than
    /// silently shadowing the second call.
    /// </para>
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">A mapping for <typeparamref name="TMessage"/> has already been registered.</exception>
    void ConfigureMapping<TProcessManagerData, TMessage>(Expression<Func<TProcessManagerData, object>> processManagerProperty, Expression<Func<TMessage, object>> messageExpression)
        where TProcessManagerData : IProcessManagerData
        where TMessage : Message;
}
