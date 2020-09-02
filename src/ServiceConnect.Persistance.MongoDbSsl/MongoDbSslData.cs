using MongoDB.Bson.Serialization.Attributes;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    [BsonIgnoreExtraElements]
    public class MongoDbSslData<T> : IPersistanceData<T>
    {
        public int Version { get; set; }
        public T Data { get; set; }
        public string Name { get; set; }
        public bool Locked { get; set; }
    }
}
