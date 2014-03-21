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
            public int Version { get; set; }
            public string Name {get; set;}
        }

        readonly Guid _correlationId = Guid.NewGuid();

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData {CorrelationId = _correlationId, Name = "TestData"};
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();

            // Act
            processManagerFinder.InsertData(new VersionData<IProcessManagerData> { Data = data });

            // Assert
            Assert.Equal("TestData", processManagerFinder.FindData<TestData>(_correlationId).Data.Name);
        }

        [Fact]
        public void ShouldThrowWhenInsertingDataWithExistingId()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerData dataWithDuplicateId = new TestData { CorrelationId = _correlationId, Name = "TestDataWithDuplicateId" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();
            processManagerFinder.InsertData(new VersionData<IProcessManagerData> { Data = data });

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.InsertData(new VersionData<IProcessManagerData> { Data = dataWithDuplicateId}));
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerData dataUpdated = new TestData { CorrelationId = _correlationId, Name = "TestDataUpdated" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();
            processManagerFinder.InsertData(new VersionData<IProcessManagerData> { Data = data });

            // Act
            processManagerFinder.UpdateData(new VersionData<IProcessManagerData> { Data = dataUpdated });

            // Assert
            Assert.Equal("TestDataUpdated", processManagerFinder.FindData<TestData>(_correlationId).Data.Name);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingDataThatDoesNotExist()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(new VersionData<IProcessManagerData> { Data = data }));
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();
            processManagerFinder.InsertData(new VersionData<IProcessManagerData> { Data = data });

            // Act
            processManagerFinder.DeleteData(new VersionData<IProcessManagerData> { Data = data });

            // Assert
            Assert.Null(processManagerFinder.FindData<IProcessManagerData>(_correlationId).Data);
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder();

            // Act
            var result = processManagerFinder.FindData<IProcessManagerData>(_correlationId);

            // Assert
            Assert.Null(result.Data);
        }
    }
}
