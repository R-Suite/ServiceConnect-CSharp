namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// Configures MongoDB persistence integration for ServiceConnect.
/// </summary>
public sealed class MongoDbPersistenceOptions
{
    /// <summary>
    /// MongoDB connection string. Must be explicitly configured; there is no default,
    /// to prevent accidental localhost use in production.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the MongoDB database name used for persisted records.
    /// </summary>
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";

    /// <summary>
    /// Gets or sets optional SSL/TLS settings for the MongoDB connection.
    /// </summary>
    public MongoDbSslOptions? Ssl { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of timeouts claimed per poll by the
    /// timeout store. Keeps a single poll bounded under load — the unclaimed
    /// due rows are picked up on the next poll. Must be positive.
    /// </summary>
    public int TimeoutBatchSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets the lease duration applied when claiming a timeout for
    /// dispatch. Shorter leases recover faster from crashed handlers; longer
    /// leases are safer for handlers with variable dispatch latency. Must
    /// be positive.
    /// </summary>
    public TimeSpan TimeoutLockLeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
