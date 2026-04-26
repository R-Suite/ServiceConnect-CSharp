namespace ServiceConnect.Filters.MessageDeduplication;

/// <summary>
/// Identifies the storage backend used to persist processed message IDs for deduplication.
/// </summary>
public enum PersistorType
{
    /// <summary>
    /// In-process, in-memory store. Suitable for development and single-instance deployments;
    /// state is lost on restart and is not shared across instances.
    /// </summary>
    InMemory,

    /// <summary>
    /// MongoDB-backed store. Supports optional SSL/TLS client certificate authentication
    /// via <see cref="DeduplicationFilterSettings.MongoDbCertPath"/> or
    /// <see cref="DeduplicationFilterSettings.MongoDbCertBase64"/>.
    /// </summary>
    MongoDb
}
