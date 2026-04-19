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

/// <summary>
/// Describes a persisted aggregator snapshot, including any records that could not be resolved.
/// </summary>
public interface IAggregatorSnapshot
{
    /// <summary>
    /// Gets the stored messages that were successfully resolved back into CLR objects.
    /// </summary>
    IReadOnlyList<object> ResolvedMessages { get; }

    /// <summary>
    /// Gets the storage ids for the resolved messages.
    /// </summary>
    IReadOnlyList<Guid> ResolvedIds { get; }

    /// <summary>
    /// Gets the number of stored records that could not be resolved.
    /// </summary>
    int UnresolvedCount { get; }
}
