namespace ServiceConnect.Interfaces;

/// <summary>
/// Persists scheduled timeout messages for later dispatch.
/// </summary>
/// <remarks>
/// Lease semantics — consistent across all <see cref="ITimeoutStore"/> implementations:
/// <list type="bullet">
/// <item>Remove and release operations accept an optional lock owner. When supplied,
/// the operation is lease-checked.</item>
/// <item>A worker passing a non-null <c>lockOwner</c> must hold an unexpired lease for
/// the row. An expired-but-not-yet-reaped lease is treated as already invalidated.</item>
/// <item>A reaper (or the natural lease-expiry path) wins any race with a worker; the
/// worker observes <see cref="Exceptions.ConcurrencyException"/>.</item>
/// <item>When the lock owner is null, the operation is unconditional and never throws
/// <see cref="Exceptions.ConcurrencyException"/>.</item>
/// </list>
/// </remarks>
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
    /// <param name="batchSize">
    /// When supplied, limits the number of timeouts returned in a single poll. Must be
    /// greater than zero when supplied; null leaves cap behaviour to the persistor's
    /// default (MongoDb uses the configured <c>TimeoutBatchSize</c>; InMemory returns all
    /// due timeouts).
    /// </param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="batchSize"/> is non-null and not greater than zero.
    /// </exception>
    Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a timeout after it has been dispatched.
    /// </summary>
    /// <param name="id">The timeout identifier.</param>
    /// <param name="lockOwner">
    /// When non-null, the row is removed only if its current lock owner matches; when null,
    /// the row is removed unconditionally.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when <paramref name="lockOwner"/> is supplied and the row's current owner
    /// does not match.
    /// </exception>
    Task RemoveDispatchedTimeoutAsync(
        Guid id,
        Guid? lockOwner = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a dispatched timeout so it may be retried later.
    /// </summary>
    /// <param name="id">The timeout identifier.</param>
    /// <param name="lockOwner">
    /// When non-null, the row is released only if its current lock owner matches; when null,
    /// the row is released unconditionally.
    /// </param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <exception cref="Exceptions.ConcurrencyException">
    /// Thrown when <paramref name="lockOwner"/> is supplied and the row's current owner
    /// does not match.
    /// </exception>
    Task ReleaseDispatchedTimeoutAsync(
        Guid id,
        Guid? lockOwner = null,
        CancellationToken cancellationToken = default);
}
