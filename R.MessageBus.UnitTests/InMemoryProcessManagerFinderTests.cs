using System;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.InMemory;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class InMemoryProcessManagerFinderTests
    {
        public class TestData : IProcessManagerData
        {
            public Guid CorrelationId { get; set; }
            public string Name {get; set;}
        }

        readonly Guid _correlarionId = Guid.NewGuid();

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData {CorrelationId = _correlarionId, Name = "TestData"};
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            Assert.Equal("TestData", ((TestData)processManagerFinder.FindData<IProcessManagerData>(_correlarionId)).Name);
        }

        [Fact]
        public void ShouldThrowWhenInsertingDataWithExistingId()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlarionId, Name = "TestData" };
            IProcessManagerData dataWithDuplicateId = new TestData { CorrelationId = _correlarionId, Name = "TestDataWithDuplicateId" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();
            processManagerFinder.InsertData(data);

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.InsertData(dataWithDuplicateId));
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlarionId, Name = "TestData" };
            IProcessManagerData dataUpdated = new TestData { CorrelationId = _correlarionId, Name = "TestDataUpdated" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();
            processManagerFinder.InsertData(data);

            // Act
            processManagerFinder.UpdateData(dataUpdated);

            // Assert
            Assert.Equal("TestDataUpdated", ((TestData)processManagerFinder.FindData<IProcessManagerData>(_correlarionId)).Name);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingDataThatDoesNotExist()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlarionId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(data));
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlarionId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();
            processManagerFinder.InsertData(data);

            // Act
            processManagerFinder.DeleteData(data);

            // Assert
            Assert.Null(processManagerFinder.FindData<IProcessManagerData>(_correlarionId));
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();

            // Act
            var result = processManagerFinder.FindData<IProcessManagerData>(_correlarionId);

            // Assert
            Assert.Null(result);
        }
    }
}
