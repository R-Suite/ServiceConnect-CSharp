using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;

namespace ServiceConnect.Filters.MessageDeduplication
{
    public static class PersistorFactory
    {
        public static IMessageDeduplicationPersistor Create(PersistorType type) => type switch
        {
            PersistorType.InMemory => new MessageDeduplicationPersistorInMemory(),
            PersistorType.MongoDb => new MessageDeduplicationPersistorMongoDb(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported persistor type.")
        };
    }
}
