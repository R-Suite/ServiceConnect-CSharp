namespace ServiceConnect.Interfaces;

/// <summary>
/// Persists scheduled timeout messages for later dispatch.
/// </summary>
public interface ITimeoutStore
{
    /// <summary>
    /// Inserts a timeout into the store.
    /// </summary>
    /// <param name="timeoutData">The timeout to persist.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the next batch of due timeouts.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The due timeouts and the next recommended poll time.</returns>
    Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a timeout after it has been dispatched successfully.
    /// </summary>
    /// <param name="id">The timeout identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a dispatched timeout so it may be retried later.
    /// </summary>
    /// <param name="id">The timeout identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default);
}
