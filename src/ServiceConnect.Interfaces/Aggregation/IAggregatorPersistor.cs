namespace ServiceConnect.Interfaces;

/// <summary>
/// Persists the buffered state used by aggregators between message deliveries.
/// </summary>
public interface IAggregatorPersistor
{
    /// <summary>
    /// Stores an aggregated message for the named aggregator instance.
    /// </summary>
    /// <param name="data">The message payload to persist.</param>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all persisted messages for the named aggregator.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The persisted messages.</returns>
    Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default);

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
    /// Counts the number of persisted messages for the named aggregator.
    /// </summary>
    /// <param name="name">The logical aggregator name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The number of stored records.</returns>
    Task<int> CountAsync(string name, CancellationToken cancellationToken = default);
}
