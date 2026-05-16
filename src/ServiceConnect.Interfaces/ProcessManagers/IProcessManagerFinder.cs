namespace ServiceConnect.Interfaces;

/// <summary>
/// Finds and persists process-manager state.
/// </summary>
public interface IProcessManagerFinder
{
    /// <summary>
    /// Finds the persisted process-manager state that matches an incoming message.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="mapper">The property mapper describing correlation rules.</param>
    /// <param name="message">The incoming message.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The persisted data wrapper, or <see langword="null"/> when no match exists.</returns>
    /// <remarks>
    /// <para>
    /// <b>Fresh-copy contract.</b> The returned <see cref="IPersistenceData{T}.Data"/> reference
    /// MUST be a fresh copy per call, independent of any cached storage. Callers (notably
    /// <c>ProcessManagerProcessor</c>'s dispatch loop) freely mutate <c>Data</c> in handler scope;
    /// the persistence layer must guarantee that a subsequent <see cref="FindDataAsync"/>
    /// invocation observes the previously-stored state, not the in-flight mutation. Implementors
    /// that cache rows internally MUST clone (or otherwise materialise a fresh graph) before returning.
    /// </para>
    /// <para>
    /// Built-in implementations comply: <c>InMemoryProcessManagerFinder</c> deep-clones via
    /// <c>DeepClone.Clone</c>; <c>MongoDbProcessManagerFinder</c> relies on BSON deserialization
    /// to produce a fresh CLR object per query.
    /// </para>
    /// </remarks>
    Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;

    /// <summary>
    /// Inserts new process-manager state.
    /// </summary>
    /// <param name="data">The process-manager state to insert.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates existing process-manager state.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="data">The persisted data wrapper to update.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task UpdateDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;

    /// <summary>
    /// Deletes persisted process-manager state. Use this to physically complete a saga and
    /// remove its row from the store; the framework does NOT call this automatically — saga
    /// completion is a deliberate decision the application owns.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="data">The persisted data wrapper to delete.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <remarks>
    /// <para>
    /// <b>Saga completion patterns.</b> Two approaches:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Flag-based completion (default).</b> Set a boolean on the data
    /// (<c>data.IsCompleted = true</c>); handlers check the flag on entry and return early. The
    /// row lives in the store indefinitely — useful for audit, but consumes storage. This is
    /// what the framework's built-in dispatch loop and the bundled examples do; no
    /// <c>DeleteDataAsync</c> call required.
    /// </item>
    /// <item>
    /// <b>Physical deletion via <see cref="DeleteDataAsync"/>.</b> Resolve
    /// <see cref="IProcessManagerFinder"/> from DI inside the handler and call this method to
    /// remove the row. After deletion, a late-arriving message or timeout for the same
    /// correlation id sees no saga and starts a fresh one. Reserve for sagas whose completion
    /// is final and replay-safe. The framework's success-path persist re-checks for the row
    /// before issuing UpdateData; a handler that deleted the saga mid-invocation and then
    /// returned cleanly will NOT have its deletion silently undone by an update that
    /// resurrects the just-deleted row.
    /// </item>
    /// </list>
    /// <para>
    /// All first-party persistors enforce optimistic concurrency on delete (filter on
    /// <c>Version</c>) and throw <see cref="Exceptions.ConcurrencyException"/> when the row is
    /// missing or stale — a delete cannot silently lose a concurrent update.
    /// </para>
    /// </remarks>
    Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
}
