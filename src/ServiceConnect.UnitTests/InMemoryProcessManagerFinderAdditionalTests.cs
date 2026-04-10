using System;
using System.Collections.Generic;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests
{
    // TestData and TestProcessManagerPropertyMapper are already defined in InMemoryProcessManagerFinderTests.cs

    public class InMemoryProcessManagerFinderAdditionalTests
    {
        private static TimeoutData MakeTimeoutData(Guid id, DateTime time) => new TimeoutData
        {
            Id = id,
            Time = time,
            Headers = new Dictionary<string, object>()
        };

        [Fact]
        public void InsertTimeout_StoresTimeoutData()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddHours(-1)));

            var batch = finder.GetTimeoutsBatch();
            Assert.Single(batch.DueTimeouts);
            Assert.Equal(id, batch.DueTimeouts[0].Id);
        }

        [Fact]
        public void InsertTimeout_ThrowsWhenDuplicateId()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddMinutes(5)));

            Assert.Throws<ArgumentException>(() => finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddMinutes(10))));
        }

        [Fact]
        public void InsertTimeout_RaisesTimeoutInsertedEvent()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            DateTime? capturedTime = null;
            finder.TimeoutInserted += time => capturedTime = time;

            var expectedTime = DateTime.UtcNow.AddMinutes(5);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), expectedTime));

            Assert.Equal(expectedTime, capturedTime);
        }

        [Fact]
        public void InsertTimeout_NoTimeoutInsertedSubscriber_DoesNotThrow()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var ex = Record.Exception(() => finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(5))));

            Assert.Null(ex);
        }

        [Fact]
        public void GetTimeoutsBatch_WhenNoTimeouts_ReturnEmptyDueList()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var batch = finder.GetTimeoutsBatch();

            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public void GetTimeoutsBatch_FutureTimeout_NotInDueList()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), DateTime.UtcNow.AddHours(1)));

            var batch = finder.GetTimeoutsBatch();

            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public void GetTimeoutsBatch_FutureTimeout_SetsNextQueryTime()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var futureTime = DateTime.UtcNow.AddHours(1);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), futureTime));

            var batch = finder.GetTimeoutsBatch();

            Assert.Equal(futureTime, batch.NextQueryTime);
        }

        [Fact]
        public void GetTimeoutsBatch_NoFutureTimeouts_NextQueryTimeIsWithinOneMinute()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var batch = finder.GetTimeoutsBatch();
            var expectedMax = DateTime.UtcNow.AddMinutes(1).AddSeconds(1);

            Assert.True(batch.NextQueryTime <= expectedMax);
        }

        [Fact]
        public void GetTimeoutsBatch_PastTimeout_IsInDueList()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            finder.InsertTimeout(MakeTimeoutData(Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-1)));

            var batch = finder.GetTimeoutsBatch();

            Assert.Single(batch.DueTimeouts);
        }

        [Fact]
        public void RemoveDispatchedTimeout_RemovesTimeoutFromBatch()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);
            var id = Guid.NewGuid();
            finder.InsertTimeout(MakeTimeoutData(id, DateTime.UtcNow.AddSeconds(-1)));

            finder.RemoveDispatchedTimeout(id);

            var batch = finder.GetTimeoutsBatch();
            Assert.Empty(batch.DueTimeouts);
        }

        [Fact]
        public void RemoveDispatchedTimeout_WhenIdDoesNotExist_DoesNotThrow()
        {
            IProcessManagerFinder finder = new InMemoryProcessManagerFinder(string.Empty, string.Empty);

            var ex = Record.Exception(() => finder.RemoveDispatchedTimeout(Guid.NewGuid()));

            Assert.Null(ex);
        }
    }
}
