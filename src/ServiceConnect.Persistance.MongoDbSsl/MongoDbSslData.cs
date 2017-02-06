using System;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Persistance.MongoDbSsl
{
    public class MongoDbSslData<T> : IPersistanceData<T>
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public T Data { get; set; }
        public string Name { get; set; }
        public bool Locked { get; set; }
    }
}
