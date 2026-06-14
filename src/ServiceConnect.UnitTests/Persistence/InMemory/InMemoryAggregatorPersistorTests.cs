using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.InMemory;

/// <summary>
/// A simple IProcessManagerData implementation for aggregator tests.
/// Extends Message and implements IProcessManagerData (required by InMemoryAggregatorPersistor internals).
/// </summary>
public class AggregatorTestData(Guid correlationId) : Message(correlationId), IProcessManagerData
{
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
        IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor();
        var data = new AggregatorTestData(Guid.NewGuid()) { Value = "TestData" };

        // Act
        await aggregatorPersistor.InsertDataAsync(data, "key1", Guid.NewGuid().ToString(), CancellationToken.None);

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
        IAggregatorPersistor aggregatorPersistor = new InMemoryAggregatorPersistor();
        var data = new AggregatorTestData(corrId);
        await aggregatorPersistor.InsertDataAsync(data, "key1", Guid.NewGuid().ToString(), CancellationToken.None);

        // Act
        await aggregatorPersistor.RemoveDataAsync("key1", corrId, CancellationToken.None);

        // Assert
        Assert.Empty(await aggregatorPersistor.GetDataAsync("key1", CancellationToken.None));
    }

    [Fact]
    public async Task GetData_WhenKeyDoesNotExist_ReturnsEmptyList()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();

        var result = await persistor.GetDataAsync("nonexistent-key", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task InsertData_MultipleItems_AllRetrievable()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        var data1 = new AggregatorTestData(Guid.NewGuid()) { Value = "first" };
        var data2 = new AggregatorTestData(Guid.NewGuid()) { Value = "second" };

        await persistor.InsertDataAsync(data1, "mykey", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(data2, "mykey", Guid.NewGuid().ToString(), CancellationToken.None);

        var result = await persistor.GetDataAsync("mykey", CancellationToken.None);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task InsertData_DifferentKeys_StoredSeparately()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        var data1 = new AggregatorTestData(Guid.NewGuid()) { Value = "alpha" };
        var data2 = new AggregatorTestData(Guid.NewGuid()) { Value = "beta" };

        await persistor.InsertDataAsync(data1, "key-a", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(data2, "key-b", Guid.NewGuid().ToString(), CancellationToken.None);

        Assert.Single(await persistor.GetDataAsync("key-a", CancellationToken.None));
        Assert.Single(await persistor.GetDataAsync("key-b", CancellationToken.None));
        Assert.Equal("alpha", ((AggregatorTestData)(await persistor.GetDataAsync("key-a", CancellationToken.None))[0]).Value);
        Assert.Equal("beta", ((AggregatorTestData)(await persistor.GetDataAsync("key-b", CancellationToken.None))[0]).Value);
    }

    [Fact]
    public async Task Count_WhenKeyDoesNotExist_ReturnsZero()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();

        int count = await persistor.CountAsync("nonexistent-key", CancellationToken.None);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Count_AfterInsertingOneItem_ReturnsOne()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);

        int count = await persistor.CountAsync("mykey", CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Count_AfterInsertingMultipleItems_ReturnsCorrectCount()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);

        int count = await persistor.CountAsync("mykey", CancellationToken.None);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task Count_AfterRemovingItem_DecrementsByOne()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        var corrId = Guid.NewGuid();
        await persistor.InsertDataAsync(new AggregatorTestData(corrId), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);

        await persistor.RemoveDataAsync("mykey", corrId, CancellationToken.None);

        Assert.Equal(1, await persistor.CountAsync("mykey", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAllAsync_ClearsAllItemsForKey()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "mykey", Guid.NewGuid().ToString(), CancellationToken.None);

        await persistor.RemoveAllAsync("mykey", CancellationToken.None);

        Assert.Empty(await persistor.GetDataAsync("mykey", CancellationToken.None));
        Assert.Equal(0, await persistor.CountAsync("mykey", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAllAsync_WhenKeyDoesNotExist_DoesNotThrow()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();

        var ex = await Record.ExceptionAsync(() => persistor.RemoveAllAsync("nonexistent-key", CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RemoveAllAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new InMemoryAggregatorPersistor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.RemoveAllAsync("test", cts.Token));
    }

    [Fact]
    public async Task RemoveData_WhenKeyDoesNotExist_ThrowsConcurrencyException()
    {
        // Aggregator persistors must agree on the no-op-delete contract: a delete against a
        // mismatched key surfaces as ConcurrencyException so callers can distinguish a
        // concurrent-removal race from a structural persistence failure. Mongo and the
        // InMemoryProcessManagerFinder already follow this rule.
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => persistor.RemoveDataAsync("nonexistent-key", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RemoveData_WhenKeyExistsButCorrelationIdMismatch_ThrowsConcurrencyException()
    {
        // The (name, correlationId) row not matching any entry — even when the name bucket exists —
        // is the exact case that was silently no-op'ing. Mirror the MongoDb contract.
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "batch-mismatch", Guid.NewGuid().ToString(), CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyException>(
            () => persistor.RemoveDataAsync("batch-mismatch", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task InsertDataAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new InMemoryAggregatorPersistor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "test", Guid.NewGuid().ToString(), cts.Token));
    }

    [Fact]
    public async Task GetDataAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new InMemoryAggregatorPersistor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.GetDataAsync("test", cts.Token));
    }

    [Fact]
    public async Task RemoveDataAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new InMemoryAggregatorPersistor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.RemoveDataAsync("test", Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task CountAsync_PreCancelledToken_ThrowsOCE()
    {
        var persistor = new InMemoryAggregatorPersistor();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.CountAsync("test", cts.Token));
    }

    // --- Aggregator buffer is caller-managed: no background TTL ---

    [Fact]
    public async Task Inserted_AggregatorBuffer_StillResolvable_After3Days()
    {
        // Aggregator buffers flush via RemoveSnapshot/RemoveAll only. Background
        // expiry must never drop buffered messages mid-aggregation, regardless
        // of how long the window stays open.
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor(timeProvider);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()) { Value = "buffered" }, "slow-stream", Guid.NewGuid().ToString(), CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(3));

        var result = await persistor.GetDataAsync("slow-stream", CancellationToken.None);
        Assert.Single(result);
        Assert.Equal("buffered", ((AggregatorTestData)result[0]).Value);
    }

    /// <summary>
    /// Aggregator test data with a nested mutable collection, used to prove the
    /// in-memory persistor deep-clones rather than aliasing caller state.
    /// </summary>
    public class AggWithNested(Guid correlationId) : Message(correlationId), IProcessManagerData
    {
        public List<string> Tags { get; set; } = [];
        Guid IProcessManagerData.CorrelationId
        {
            get => base.CorrelationId;
            set { /* immutable */ }
        }
    }

    [Fact]
    public async Task InsertData_ThenMutateCallerObject_DoesNotCorruptStoredEntry()
    {
        // Insert must deep-clone so the caller's subsequent mutation
        // (including nested collections) does not leak into the buffer.
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        var data = new AggWithNested(Guid.NewGuid());
        data.Tags.Add("original");

        await persistor.InsertDataAsync(data, "nested-key", Guid.NewGuid().ToString(), CancellationToken.None);
        data.Tags.Add("after-insert-mutation");

        var result = await persistor.GetDataAsync("nested-key", CancellationToken.None);
        var stored = Assert.IsType<AggWithNested>(Assert.Single(result));
        Assert.Equal(new[] { "original" }, stored.Tags);
    }

    [Fact]
    public async Task GetData_ThenMutateReturnedObject_DoesNotCorruptStoredEntry()
    {
        // Retrieval must deep-clone so the caller mutating the returned instance
        // does not corrupt the stored copy seen by the next read.
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        var data = new AggWithNested(Guid.NewGuid());
        data.Tags.Add("original");
        await persistor.InsertDataAsync(data, "nested-key", Guid.NewGuid().ToString(), CancellationToken.None);

        var first = (AggWithNested)(await persistor.GetDataAsync("nested-key", CancellationToken.None))[0];
        first.Tags.Add("mutated-by-caller");

        var second = (AggWithNested)(await persistor.GetDataAsync("nested-key", CancellationToken.None))[0];
        Assert.Equal(new[] { "original" }, second.Tags);
    }

    [Fact]
    public void Persistor_ImplementsIDisposable_AndDisposeIsIdempotent()
    {
        // The persistor owns a CacheProvider that registers ITimer handles with the
        // TimeProvider. Dispose must release them exactly once so rebuilds of the
        // DI container do not leak timers, even when Dispose is called repeatedly.
        var persistor = new InMemoryAggregatorPersistor();
        Assert.IsAssignableFrom<IDisposable>(persistor);

        persistor.Dispose();
        var second = Record.Exception(persistor.Dispose);
        Assert.Null(second);
    }

    [Fact]
    public async Task RemoveDataAsync_NonMessageDtoWithCorrelationIdProperty_RemovesEntry()
    {
        var persistor = new InMemoryAggregatorPersistor();
        var dto = new ThirdPartyDto { CorrelationId = Guid.NewGuid(), Payload = "payload-A" };

        await persistor.InsertDataAsync(dto, "stream-A", Guid.NewGuid().ToString());
        await persistor.RemoveDataAsync("stream-A", dto.CorrelationId);

        Assert.Equal(0, await persistor.CountAsync("stream-A"));
    }

    private sealed class ThirdPartyDto : IHasCorrelationId
    {
        public Guid CorrelationId { get; init; }
        public string Payload { get; init; } = string.Empty;
    }
}
