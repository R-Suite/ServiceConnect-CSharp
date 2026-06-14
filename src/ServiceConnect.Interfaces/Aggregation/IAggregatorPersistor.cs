namespace ServiceConnect.Interfaces;

/// <summary>
/// Persists the buffered state used by aggregators between message deliveries.
/// </summary>
public interface IAggregatorPersistor
{
    /// <summary>
    /// Stores an aggregated message for the named aggregator instance, idempotent on
    /// <paramref name="idempotencyKey"/> within the aggregator's active row set.
    /// </summary>
    /// <param name="data">The message payload to persist; must be an implementation of <see cref="IHasCorrelationId"/>.</param>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="idempotencyKey">
    /// A stable per-message identifier (typically the broker-side <c>MessageId</c>) used to
    /// reject re-inserts of the same delivery. A retry-queue redelivery between Insert and
    /// the dispatcher's broker ack will re-enter <c>InsertDataAsync</c> with the same key
    /// while the prior insert's row is still buffered; the persistor must skip the second
    /// write so the aggregator's <c>Execute</c> sees each delivery exactly once. Once the
    /// row has been removed (snapshot dispatched), the key is no longer tracked.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task InsertDataAsync(IHasCorrelationId data, string name, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all persisted messages for the named aggregator.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The persisted messages.</returns>
    Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a snapshot that separates resolved and unresolved persisted records.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>A snapshot of the stored records.</returns>
    Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a single persisted message from the named aggregator.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="correlationId">The correlation id of the stored message to remove.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when the (name, correlationId) row cannot be located — either because another writer
    /// concurrently removed it or because the caller supplied a mismatched key. Callers should treat
    /// this as distinct from a structural persistence failure. All first-party persistors raise this
    /// on no-op delete; third-party implementations should follow the same contract.
    /// </exception>
    Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all persisted messages for the named aggregator.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task RemoveAllAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the records represented by a previously loaded snapshot.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="snapshot">The snapshot describing which records should be removed.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task RemoveSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the lease held by the supplied snapshot so the rows become immediately
    /// re-claimable by a subsequent <see cref="GetSnapshotAsync"/>. Called by the
    /// aggregator processor on handler failure — without an explicit release the rows
    /// would sit leased until the persistor's lease TTL expires (5 minutes on the
    /// MongoDB persistor by default), during which the next redelivery's snapshot
    /// is empty and the handler is never re-invoked.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="snapshot">The snapshot whose lease should be released.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// Default-interface-method shim: persistors that don't lease (InMemory, third-party
    /// implementations that predate this method) return immediately — the no-op semantics
    /// match a persistor where rows are always re-claimable by id alone. Persistors that
    /// stamp a <c>LockedBy</c>/<c>LockExpiresAt</c> pair on rows during snapshot acquisition
    /// (the MongoDB persistor) MUST override to clear those columns for the snapshot's
    /// session id; otherwise the handler-failure → lease-strand → silent-empty-redelivery
    /// failure mode at the processor level is unaddressed.
    /// </remarks>
    Task ReleaseSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Counts the number of persisted messages for the named aggregator.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The number of stored records.</returns>
    Task<int> CountAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts persisted messages whose CLR type is currently resolvable. Unlike
    /// <see cref="CountAsync"/> (which returns total rows including those whose CLR type
    /// could not be resolved e.g. after a type rename), this method drives the
    /// batch-size flush gate so unresolved-only batches do not trigger flushes that
    /// produce no work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default implementation delegates to <see cref="CountAsync"/>. This is correct
    /// for any persistor whose stored records are always type-resolvable (e.g. an in-memory
    /// store that holds deserialised <see cref="IHasCorrelationId"/> instances) and for
    /// any deployment where every registered type still has a live CLR mapping. It is a
    /// safe fall-back, NOT optimal: a persistor with a meaningful resolved/unresolved split
    /// (e.g. Mongo across a type-rename rollout) should override with a cheap typed
    /// predicate to avoid flushing on rows that would only count toward the gate.
    /// </para>
    /// <para>
    /// Implementers MUST NOT override with a method that mutates state. This method runs
    /// on every <c>InsertDataAsync</c> as the batch-size flush gate; an implementation that
    /// claims a lease (e.g. by delegating to <see cref="GetSnapshotAsync"/> on a
    /// snapshot-claims-lease persistor) would rotate the lease on every insert and break
    /// the per-flush lease invariant.
    /// </para>
    /// </remarks>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The number of stored records whose CLR type is currently resolvable.</returns>
    Task<int> CountResolvedAsync(string name, CancellationToken cancellationToken = default) =>
        CountAsync(name, cancellationToken);
}
