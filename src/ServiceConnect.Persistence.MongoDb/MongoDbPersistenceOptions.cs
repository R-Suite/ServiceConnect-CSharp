namespace ServiceConnect.Persistence.MongoDb;

public sealed class MongoDbPersistenceOptions
{
    /// <summary>
    /// MongoDB connection string. Must be explicitly configured; there is no default,
    /// to prevent accidental localhost use in production.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = "RMessageBusPersistentStore";
    public MongoDbSslOptions? Ssl { get; set; }
}
