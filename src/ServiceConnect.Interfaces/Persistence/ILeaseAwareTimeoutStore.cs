namespace ServiceConnect.Interfaces;

/// <summary>
/// Extends <see cref="ITimeoutStore"/> with lock-owner aware timeout release semantics.
/// </summary>
public interface ILeaseAwareTimeoutStore
{
    /// <summary>
    /// Removes a timeout only when the specified lock owner still holds it.
    /// </summary>
    /// <param name="id">The timeout identifier.</param>
    /// <param name="lockOwner">The lock owner that must match.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a timeout only when the specified lock owner still holds it.
    /// </summary>
    /// <param name="id">The timeout identifier.</param>
    /// <param name="lockOwner">The lock owner that must match.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default);
}
