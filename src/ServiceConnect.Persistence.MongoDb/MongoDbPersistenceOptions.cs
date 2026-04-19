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
}
