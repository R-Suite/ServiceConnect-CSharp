using System;
using System.Threading;
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

    public class InMemoryAggregatorPersistorTests
    {
        [Fact]
        public async Task ShouldInsertData()
        {
            // Arrange
            IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data = new AggregatorTestData(Guid.NewGuid()) { Value = "TestData" };

            // Act
            await aggregatorPersistor.InsertDataAsync(data, "key1", CancellationToken.None);

            // Assert
            var result = await aggregatorPersistor.GetDataAsync("key1", CancellationToken.None);
            Assert.Single(result);
            Assert.Equal("TestData", ((AggregatorTestData)result[0]).Value);
        }

        [Fact]
        public async Task ShouldDeleteData()
        {
            // Arrange
            var corrId = Guid.NewGuid();
            IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data = new AggregatorTestData(corrId);
            await aggregatorPersistor.InsertDataAsync(data, "key1", CancellationToken.None);

            // Act
            await aggregatorPersistor.RemoveDataAsync("key1", corrId, CancellationToken.None);

            // Assert
            Assert.Empty(await aggregatorPersistor.GetDataAsync("key1", CancellationToken.None));
        }

        [Fact]
        public async Task GetData_WhenKeyDoesNotExist_ReturnsEmptyList()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            var result = await persistor.GetDataAsync("nonexistent-key", CancellationToken.None);

            Assert.Empty(result);
        }

        [Fact]
        public async Task InsertData_MultipleItems_AllRetrievable()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data1 = new AggregatorTestData(Guid.NewGuid()) { Value = "first" };
            var data2 = new AggregatorTestData(Guid.NewGuid()) { Value = "second" };

            await persistor.InsertDataAsync(data1, "mykey", CancellationToken.None);
            await persistor.InsertDataAsync(data2, "mykey", CancellationToken.None);

            var result = await persistor.GetDataAsync("mykey", CancellationToken.None);
            Assert.Equal(2, result.Count);
        }

        [Fact]
        public async Task InsertData_DifferentKeys_StoredSeparately()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var data1 = new AggregatorTestData(Guid.NewGuid()) { Value = "alpha" };
            var data2 = new AggregatorTestData(Guid.NewGuid()) { Value = "beta" };

            await persistor.InsertDataAsync(data1, "key-a", CancellationToken.None);
            await persistor.InsertDataAsync(data2, "key-b", CancellationToken.None);

            Assert.Single(await persistor.GetDataAsync("key-a", CancellationToken.None));
            Assert.Single(await persistor.GetDataAsync("key-b", CancellationToken.None));
            Assert.Equal("alpha", ((AggregatorTestData)(await persistor.GetDataAsync("key-a", CancellationToken.None))[0]).Value);
            Assert.Equal("beta", ((AggregatorTestData)(await persistor.GetDataAsync("key-b", CancellationToken.None))[0]).Value);
        }

        [Fact]
        public async Task Count_WhenKeyDoesNotExist_ReturnsZero()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            int count = await persistor.CountAsync("nonexistent-key", CancellationToken.None);

            Assert.Equal(0, count);
        }

        [Fact]
        public async Task Count_AfterInsertingOneItem_ReturnsOne()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);

            int count = await persistor.CountAsync("mykey", CancellationToken.None);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task Count_AfterInsertingMultipleItems_ReturnsCorrectCount()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);

            int count = await persistor.CountAsync("mykey", CancellationToken.None);

            Assert.Equal(3, count);
        }

        [Fact]
        public async Task Count_AfterRemovingItem_DecrementsByOne()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            var corrId = Guid.NewGuid();
            await persistor.InsertDataAsync(new AggregatorTestData(corrId), "mykey", CancellationToken.None);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);

            await persistor.RemoveDataAsync("mykey", corrId, CancellationToken.None);

            Assert.Equal(1, await persistor.CountAsync("mykey", CancellationToken.None));
        }

        [Fact]
        public async Task RemoveAll_ClearsAllItemsForKey()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);
            await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", CancellationToken.None);

            persistor.RemoveAll("mykey");

            Assert.Empty(await persistor.GetDataAsync("mykey", CancellationToken.None));
            Assert.Equal(0, await persistor.CountAsync("mykey", CancellationToken.None));
        }

        [Fact]
        public void RemoveAll_WhenKeyDoesNotExist_DoesNotThrow()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            var ex = Record.Exception(() => persistor.RemoveAll("nonexistent-key"));

            Assert.Null(ex);
        }

        [Fact]
        public async Task RemoveData_WhenKeyDoesNotExist_DoesNotThrow()
        {
            IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);

            var ex = await Record.ExceptionAsync(() => persistor.RemoveDataAsync("nonexistent-key", Guid.NewGuid(), CancellationToken.None));

            Assert.Null(ex);
        }

        [Fact]
        public async Task InsertDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => persistor.InsertDataAsync(new object(), "test", cts.Token));
        }

        [Fact]
        public async Task GetDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => persistor.GetDataAsync("test", cts.Token));
        }

        [Fact]
        public async Task RemoveDataAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => persistor.RemoveDataAsync("test", Guid.NewGuid(), cts.Token));
        }

        [Fact]
        public async Task CountAsync_PreCancelledToken_ThrowsOCE()
        {
            var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => persistor.CountAsync("test", cts.Token));
        }
    }
}
