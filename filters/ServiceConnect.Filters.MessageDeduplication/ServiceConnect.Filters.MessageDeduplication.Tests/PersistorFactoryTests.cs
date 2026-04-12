using System;
using ServiceConnect.Filters.MessageDeduplication.Persistors;
using Xunit;

namespace ServiceConnect.Filters.MessageDeduplication.Tests
{
    public class PersistorFactoryTests
    {
        [Fact]
        public void Create_InMemory_ReturnsInMemoryPersistor()
        {
            var persistor = PersistorFactory.Create(PersistorType.InMemory);
            Assert.IsType<MessageDeduplicationPersistorInMemory>(persistor);
        }

        [Fact]
        [Trait("Category", "Docker")]
        public void Create_MongoDb_ReturnsMongoDbPersistor()
        {
            var persistor = PersistorFactory.Create(PersistorType.MongoDb);
            Assert.IsType<MessageDeduplicationPersistorMongoDb>(persistor);
        }

        [Fact]
        public void Create_InvalidType_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PersistorFactory.Create((PersistorType)999));
        }
    }
}
