using System;
using Moq;
using R.MessageBus.Core;
using R.MessageBus.Interfaces;
using R.MessageBus.Persistance.InMemory;
using Xunit;

namespace R.MessageBus.UnitTests
{
    public class TestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; }
    }
    
    public class InMemoryProcessManagerFinderTests
    {

        readonly Guid _correlationId = Guid.NewGuid();
        private readonly IProcessManagerPropertyMapper _mapper;

        public InMemoryProcessManagerFinderTests()
        {
            _mapper = new ProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData {CorrelationId = _correlationId, Name = "TestData"};
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            Assert.Equal("TestData", processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId)).Data.Name);
        }

        [Fact]
        public void ShouldThrowWhenInsertingDataWithExistingId()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerData dataWithDuplicateId = new TestData { CorrelationId = _correlationId, Name = "TestDataWithDuplicateId" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            processManagerFinder.InsertData(data);

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.InsertData(dataWithDuplicateId));
        }

        [Fact]
        public void ShouldUpdateData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerData dataUpdated = new TestData { CorrelationId = _correlationId, Name = "TestDataUpdated" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            processManagerFinder.InsertData(data);

            // Act
            processManagerFinder.UpdateData(new MemoryData<IProcessManagerData> { Data = dataUpdated, Version = 1});

            // Assert
            Assert.Equal("TestDataUpdated", processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId)).Data.Name);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingDataThatDoesNotExist()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(new MemoryData<IProcessManagerData> { Data = data }));
        }

        [Fact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            processManagerFinder.InsertData(data1);

            var foundData1 = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));
            var foundData2 = processManagerFinder.FindData<TestData>(_mapper, new Message(_correlationId));

            processManagerFinder.UpdateData(foundData1); // first update should be fine

            // Act / Assert
            Assert.Throws<ArgumentException>(() => processManagerFinder.UpdateData(foundData2)); // second update should fail
        }

        [Fact]
        public void ShouldDeleteData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            processManagerFinder.InsertData(data);

            // Act
            processManagerFinder.DeleteData(new MemoryData<IProcessManagerData> { Data = data });

            // Assert
            Assert.Null(processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId)).Data);
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act
            var result = processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Null(result.Data);
        }
    }
}
