using System;
using System.Collections.Generic;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class TestData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
        public string Name { get; set; } = "";
    }

    public class InMemoryProcessManagerFinderTests
    {
        readonly Guid _correlationId = Guid.NewGuid();
        private readonly IProcessManagerPropertyMapper _mapper;

        public InMemoryProcessManagerFinderTests()
        {
            _mapper = new TestProcessManagerPropertyMapper();
            _mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        }

        [Fact]
        public void ShouldInsertData()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act
            processManagerFinder.InsertData(data);

            // Assert
            // InsertData wraps as MemoryData<IProcessManagerData>, so FindData must use IProcessManagerData
            var found = processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId));
            Assert.NotNull(found);
            Assert.Equal("TestData", ((TestData)found.Data).Name);
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
            processManagerFinder.UpdateData(new MemoryData<IProcessManagerData> { Data = dataUpdated, Version = 1 });

            // Assert
            var found = processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId));
            Assert.NotNull(found);
            Assert.Equal("TestDataUpdated", ((TestData)found.Data).Name);
        }

        [Fact]
        public void ShouldThrowWhenUpdatingDataThatDoesNotExist()
        {
            // Arrange
            IProcessManagerData data = new TestData { CorrelationId = _correlationId, Name = "TestData" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act / Assert
            Assert.Throws<PersistenceException>(() => processManagerFinder.UpdateData(new MemoryData<IProcessManagerData> { Data = data }));
        }

        [Fact]
        public void ShouldThrowWhenUpdatingTwoInstancesOfSameDataAtTheSameTime()
        {
            // Arrange
            IProcessManagerData data1 = new TestData { CorrelationId = _correlationId, Name = "TestData1" };
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            processManagerFinder.InsertData(data1);

            var foundData1 = (MemoryData<IProcessManagerData>)processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId))!;
            var foundData2 = (MemoryData<IProcessManagerData>)processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId))!;

            var foundData1Temp = new MemoryData<IProcessManagerData> { Data = foundData1.Data, Version = foundData1.Version };
            var foundData2Temp = new MemoryData<IProcessManagerData> { Data = foundData2.Data, Version = foundData2.Version };

            processManagerFinder.UpdateData(foundData1Temp); // first update should be fine

            // Act / Assert
            Assert.Throws<PersistenceException>(() => processManagerFinder.UpdateData(foundData2Temp)); // second update should fail
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
            Assert.Null(processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId)));
        }

        [Fact]
        public void ShouldReturnNullWhenDataNotFound()
        {
            // Arrange
            IProcessManagerFinder processManagerFinder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            // Act
            var result = processManagerFinder.FindData<IProcessManagerData>(_mapper, new Message(_correlationId));

            // Assert
            Assert.Null(result);
        }

        // --- Timeout tests ---

        private static TimeoutData MakeTimeoutData(Guid id, DateTime time) => new TimeoutData
        {
            Id = id,
            Time = time,
            Headers = new Dictionary<string, object>()
        };

        [Fact]
        public void InsertTimeout_StoresTimeoutData()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddHours(-1)));

            var batch = finder.GetTimeoutsBatch();
            Assert.Single(batch.DueTimeouts);
            Assert.Equal(id, batch.DueTimeouts[0].Id);
        }

        [Fact]
        public void InsertTimeout_ThrowsWhenDuplicateId()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddMinutes(5)));

            Assert.Throws<ArgumentException>(() => finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddMinutes(10))));
        }

        [Fact]
        public void InsertTimeout_RaisesTimeoutInsertedEvent()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            DateTime? capturedTime = null;
            finder.TimeoutInserted += time => capturedTime = time;

            var expectedTime = DateTime.UtcNow.AddMinutes(5);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), expectedTime));

            Assert.Equal(expectedTime, capturedTime);
        }

        [Fact]
        public void InsertTimeout_NoTimeoutInsertedSubscriber_DoesNotThrow()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var ex = Record.Exception(() => finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5))));

            Assert.Null(ex);
        }

        [Fact]
        public void GetTimeoutsBatch_WhenNoTimeouts_ReturnEmptyDueList()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var batch = finder.GetTimeoutsBatch();

            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public void GetTimeoutsBatch_FutureTimeout_NotInDueList()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), DateTime.UtcNow.AddHours(1)));

            var batch = finder.GetTimeoutsBatch();

            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public void GetTimeoutsBatch_FutureTimeout_SetsNextQueryTime()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var futureTime = DateTime.UtcNow.AddHours(1);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), futureTime));

            var batch = finder.GetTimeoutsBatch();

            Assert.Equal(futureTime, batch.NextQueryTime);
        }

        [Fact]
        public void GetTimeoutsBatch_NoFutureTimeouts_NextQueryTimeIsWithinOneMinute()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var batch = finder.GetTimeoutsBatch();
            var expectedMax = DateTime.UtcNow.AddMinutes(1).AddSeconds(1);

            Assert.True(batch.NextQueryTime <= expectedMax);
        }

        [Fact]
        public void GetTimeoutsBatch_PastTimeout_IsInDueList()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-1)));

            var batch = finder.GetTimeoutsBatch();

            Assert.Single(batch.DueTimeouts);
        }

        [Fact]
        public void RemoveDispatchedTimeout_RemovesTimeoutFromBatch()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddSeconds(-1)));

            finder.RemoveDispatchedTimeout(id);

            var batch = finder.GetTimeoutsBatch();
            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public void RemoveDispatchedTimeout_WhenIdDoesNotExist_DoesNotThrow()
        {
            ITimeoutStore finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var ex = Record.Exception(() => finder.RemoveDispatchedTimeout(Guid.NewGuid()));

            Assert.Null(ex);
        }
    }

    /// <summary>
    /// Minimal IProcessManagerPropertyMapper implementation for tests,
    /// replacing the old ProcessManagerPropertyMapper from ServiceConnect.Core.
    /// </summary>
    public class TestProcessManagerPropertyMapper : IProcessManagerPropertyMapper
    {
        public List<ProcessManagerToMessageMap> Mappings { get; set; } = new();

        public void ConfigureMapping<TProcessManagerData, TMessage>(
            System.Linq.Expressions.Expression<Func<TProcessManagerData, object>> processManagerProperty,
            System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
            where TProcessManagerData : IProcessManagerData
        {
            var map = new ProcessManagerToMessageMap
            {
                MessageType = typeof(TMessage),
                PropertiesHierarchy = new Dictionary<string, Type>(),
                MessageProp = BuildMessageFunc(messageExpression)
            };

            // Extract property hierarchy from processManagerProperty
            var body = processManagerProperty.Body;
            if (body is System.Linq.Expressions.UnaryExpression unary)
                body = unary.Operand;

            if (body is System.Linq.Expressions.MemberExpression member)
            {
                var propInfo = (System.Reflection.PropertyInfo)member.Member;
                map.PropertiesHierarchy[propInfo.Name] = propInfo.PropertyType;
            }

            Mappings.Add(map);
        }

        private static Func<object, object> BuildMessageFunc<TMessage>(
            System.Linq.Expressions.Expression<Func<TMessage, object>> messageExpression)
        {
            var compiled = messageExpression.Compile();
            return obj => compiled((TMessage)obj);
        }
    }
}
