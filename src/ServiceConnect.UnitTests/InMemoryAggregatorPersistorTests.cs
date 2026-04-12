using System;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests
{
    /// <summary>
    /// A simple IProcessManagerData implementation for aggregator tests.
    /// Extends Message and implements IProcessManagerData (required by InMemoryAggregatorPersistor internals).
    /// </summary>
    public class AggregatorTestData : Message, IProcessManagerData
    {
        public AggregatorTestData(Guid correlationId) : base(correlationId) { }
        public string Value { get; set; } = "";

        // Explicit interface implementation to satisfy IProcessManagerData.CorrelationId { get; set; }
        // while Message.CorrelationId only has a getter.
        Guid IProcessManagerData.CorrelationId
        {
            get => base.CorrelationId;
            set { /* Message CorrelationId is immutable; set via constructor */ }
        }
    }

    public class InMemoryAggregatorPersistorTests
    {
        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data = new AggregatorTestData(Guid.NewGuid()) { Value = "TestData" };

            // Act
            aggregatorPersistor.InsertData(data, "key1");

            // Assert
            var result = aggregatorPersistor.GetData("key1");
            Assert.Single(result);
            Assert.Equal("TestData", ((AggregatorTestData)result[0]).Value);
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            var corrId = Guid.NewGuid();
            IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data = new AggregatorTestData(corrId);
            aggregatorPersistor.InsertData(data, "key1");

            // Act
            aggregatorPersistor.RemoveData("key1", corrId);

            // Assert
            Assert.Empty(aggregatorPersistor.GetData("key1"));
        }

        [Fact]
        public void GetData_WhenKeyDoesNotExist_ReturnsEmptyList()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            var result = persistor.GetData("nonexistent-key");

            Assert.Empty(result);
        }

        [Fact]
        public void InsertData_MultipleItems_AllRetrievable()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data1 = new AggregatorTestData(Guid.NewGuid()) { Value = "first" };
            var data2 = new AggregatorTestData(Guid.NewGuid()) { Value = "second" };

            persistor.InsertData(data1, "mykey");
            persistor.InsertData(data2, "mykey");

            var result = persistor.GetData("mykey");
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public void InsertData_DifferentKeys_StoredSeparately()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data1 = new AggregatorTestData(Guid.NewGuid()) { Value = "alpha" };
            var data2 = new AggregatorTestData(Guid.NewGuid()) { Value = "beta" };

            persistor.InsertData(data1, "key-a");
            persistor.InsertData(data2, "key-b");

            Assert.Single(persistor.GetData("key-a"));
            Assert.Single(persistor.GetData("key-b"));
            Assert.Equal("alpha", ((AggregatorTestData)persistor.GetData("key-a")[0]).Value);
            Assert.Equal("beta", ((AggregatorTestData)persistor.GetData("key-b")[0]).Value);
        }

        [Fact]
        public void Count_WhenKeyDoesNotExist_ReturnsZero()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            int count = persistor.Count("nonexistent-key");

            Assert.Equal(0, count);
        }

        [Fact]
        public void Count_AfterInsertingOneItem_ReturnsOne()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");

            int count = persistor.Count("mykey");

            Assert.Equal(1, count);
        }

        [Fact]
        public void Count_AfterInsertingMultipleItems_ReturnsCorrectCount()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");

            int count = persistor.Count("mykey");

            Assert.Equal(3, count);
        }

        [Fact]
        public void Count_AfterRemovingItem_DecrementsByOne()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var corrId = Guid.NewGuid();
            persistor.InsertData(new AggregatorTestData(corrId), "mykey");
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");

            persistor.RemoveData("mykey", corrId);

            Assert.Equal(1, persistor.Count("mykey"));
        }

        [Fact]
        public void RemoveAll_ClearsAllItemsForKey()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");
            persistor.InsertData(new AggregatorTestData(Guid.NewGuid()), "mykey");

            persistor.RemoveAll("mykey");

            Assert.Empty(persistor.GetData("mykey"));
            Assert.Equal(0, persistor.Count("mykey"));
        }

        [Fact]
        public void RemoveAll_WhenKeyDoesNotExist_DoesNotThrow()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            var ex = Record.Exception(() => persistor.RemoveAll("nonexistent-key"));

            Assert.Null(ex);
        }

        [Fact]
        public void RemoveData_WhenKeyDoesNotExist_DoesNotThrow()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            var ex = Record.Exception(() => persistor.RemoveData("nonexistent-key", Guid.NewGuid()));

            Assert.Null(ex);
        }
    }
}
