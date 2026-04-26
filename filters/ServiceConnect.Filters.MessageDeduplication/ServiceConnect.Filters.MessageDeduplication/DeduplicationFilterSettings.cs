namespace ServiceConnect.Filters.MessageDeduplication;

/// <summary>
/// Options for the message deduplication filter. Register via
/// <see cref="AddMessageDeduplicationFilterExtensions.AddMessageDeduplicationFilter"/>.
/// </summary>
public sealed class DeduplicationFilterSettings
{
    public int MsgExpiryHours { get; set; } = 24;

    /// <summary>
    /// How often to clean up expired messages from the persistance store.
    /// </summary>
    public int MsgCleanupIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// MongoDb persistance store connection string.
    /// </summary>
    public string? ConnectionStringMongoDb { get; set; } = "mongodb://localhost";

    /// <summary>
    /// Name of the MongoDb database.
    /// </summary>
    public string? DatabaseNameMongoDb { get; set; } = "ServiceConnect-Filters-MessageDeduplication";

    /// <summary>
    /// Name of the MongoDb collection.
    /// </summary>
    public string? CollectionNameMongoDb { get; set; } = "ProcessedMessages";

    /// <summary>
    /// Path to X509 certificate file for MongoDB SSL client authentication.
    /// If set, TLS is auto-enabled on the connection.
    /// Takes precedence over MongoDbCertBase64 if both are set.
    /// </summary>
    public string? MongoDbCertPath { get; set; }

    /// <summary>
    /// Base64-encoded X509 certificate for MongoDB SSL client authentication.
    /// Alternative to MongoDbCertPath for environments where file paths are impractical.
    /// </summary>
    public string? MongoDbCertBase64 { get; set; }

    /// <summary>
    /// Password for the X509 certificate (optional).
    /// Used with both MongoDbCertPath and MongoDbCertBase64.
    /// </summary>
    public string? MongoDbCertPassphrase { get; set; }

    /// <summary>
    /// Which persistor backend to use for message deduplication.
    /// </summary>
    public PersistorType PersistorType { get; set; } = PersistorType.InMemory;

    /// <summary>
    /// Disable message expiry. Processed messages in the persistance store won't get deleted.
    /// </summary>
    public bool DisableMsgExpiry { get; set; }
}
