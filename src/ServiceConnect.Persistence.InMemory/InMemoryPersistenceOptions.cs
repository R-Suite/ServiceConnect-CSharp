namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Configuration for the in-memory persistence stores.
/// </summary>
public sealed class InMemoryPersistenceOptions
{
    /// <summary>
    /// Lease duration applied when claiming a timeout for dispatch via
    /// <see cref="ServiceConnect.Interfaces.ITimeoutStore.GetTimeoutsBatchAsync"/>.
    /// Mirrors <c>MongoDbPersistenceOptions.TimeoutLockLeaseDuration</c>. Must be positive.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
}
