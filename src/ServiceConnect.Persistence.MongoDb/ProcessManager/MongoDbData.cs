using MongoDB.Bson.Serialization.Attributes;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// MongoDB persistence wrapper for process-manager state and its version metadata.
/// </summary>
/// <typeparam name="T">The process-manager data type.</typeparam>
[BsonIgnoreExtraElements]
internal sealed class MongoDbData<T> : IPersistenceData<T>, IVersioned, IIdentified where T : class, IProcessManagerData
{
    /// <summary>
    /// Gets or sets the persistence record identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public long Version { get; set; }

    /// <inheritdoc />
    public required T Data { get; set; }
}
