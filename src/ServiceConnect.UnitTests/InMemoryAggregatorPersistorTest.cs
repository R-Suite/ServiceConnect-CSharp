using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    public class InMemoryAggregatorPersistorTest
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
    }
}
