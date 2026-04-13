using MongoDB.Bson.Serialization.Attributes;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistence.MongoDb;

[BsonIgnoreExtraElements]
public sealed class MongoDbData<T> : IPersistenceData<T>, IVersioned where T : class, IProcessManagerData
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public T Data { get; set; } = default!;
    public string Name { get; set; } = string.Empty;
    public bool Locked { get; set; }
}
