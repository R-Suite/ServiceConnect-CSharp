using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistance.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class InMemoryAggregatorPersistorTest
    {
        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty);

            // Act
            aggregatorPersistor.InsertData("TestData", "key1");

            // Assert
            Assert.Equal("TestData", aggregatorPersistor.GetData("key1")[0]);
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            var corrId = Guid.NewGuid();
            IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty);
            aggregatorPersistor.InsertData(new Message(corrId), "key1");

            // Act
            aggregatorPersistor.RemoveData("key1", corrId);

            // Assert
            Assert.Equal(0, aggregatorPersistor.GetData("key1").Count);
        }
    }
}
