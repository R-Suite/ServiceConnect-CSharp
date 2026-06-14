namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// Configuration for the in-memory persistence stores. Populated via the
/// <c>configure</c> callback passed to <c>UseInMemoryPersistence(opts =&gt; …)</c>;
/// properties are <c>get; set;</c> rather than <c>init</c> so callbacks can mutate
/// them post-construction (the callback runs after <c>new InMemoryPersistenceOptions()</c>
/// inside <c>UseInMemoryPersistence</c>, which is not an init-context). Matches the
/// shape of <c>MongoDbPersistenceOptions</c>.
/// </summary>
public sealed class InMemoryPersistenceOptions
{
    /// <summary>
    /// Lease duration applied when claiming a timeout for dispatch via
    /// <see cref="ServiceConnect.Interfaces.ITimeoutStore.GetTimeoutsBatchAsync"/>.
    /// Mirrors <c>MongoDbPersistenceOptions.TimeoutLockLeaseDuration</c>. Must be positive.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
