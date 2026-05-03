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
    /// Deletes persisted process-manager state.
    /// </summary>
    /// <typeparam name="T">The process-manager data type.</typeparam>
    /// <param name="data">The persisted data wrapper to delete.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData;
}
