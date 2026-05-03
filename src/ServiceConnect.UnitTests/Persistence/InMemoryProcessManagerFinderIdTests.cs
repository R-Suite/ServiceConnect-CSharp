using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

// Minimal saga data type scoped to this file; reuses the CorrelationId mapping
// via Message so we can exercise the full Insert → Find → Update roundtrip.
file sealed class IdTestData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public string Value { get; set; } = "";
}

public class InMemoryProcessManagerFinderIdTests
{
    private static (InMemoryProcessManagerFinder finder, IProcessManagerPropertyMapper mapper) Build()
    {
        var cache = new ProcessManagerPredicateCache();
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        var finder = new InMemoryProcessManagerFinder(cache, state);
        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<IdTestData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        return (finder, mapper);
    }

    [Fact]
    public async Task InsertData_StampsStableId()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var (finder, mapper) = Build();
        await finder.InsertDataAsync(new IdTestData { CorrelationId = correlationId, Value = "initial" }, CancellationToken.None);

        // Act — first retrieval
        var first = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(first);

        // IIdentified surfaces the Id without requiring a cast to the concrete wrapper type.
        var id = ((IIdentified)first).Id;

        // Assert — insert must stamp a non-empty Id
        Assert.NotEqual(Guid.Empty, id);

        // Act — second retrieval (same stored row, no mutation)
        var second = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(second);
        var id2 = ((IIdentified)second).Id;

        // Assert — Id must be stable across independent reads
        Assert.Equal(id, id2);
    }

    [Fact]
    public async Task Id_RoundTripsAcrossUpdate()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var (finder, mapper) = Build();
        await finder.InsertDataAsync(new IdTestData { CorrelationId = correlationId, Value = "initial" }, CancellationToken.None);

        var inserted = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(inserted);
        var originalId = ((IIdentified)inserted).Id;
        Assert.NotEqual(Guid.Empty, originalId);

        // Act — update the saga
        inserted.Data.Value = "updated";
        await finder.UpdateDataAsync(inserted, CancellationToken.None);

        // Re-retrieve after update
        var afterUpdate = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(afterUpdate);
        var updatedId = ((IIdentified)afterUpdate).Id;

        // Assert — Id must survive the update; pre-fix this would be Guid.Empty
        Assert.Equal(originalId, updatedId);
        Assert.Equal("updated", afterUpdate.Data.Value);
    }

    [Fact]
    public async Task Id_StableAcrossMultipleUpdates()
    {
        // Mirrors the Mongo persistor's contract that _id is stamped once and never
        // overwritten: a second UpdateDataAsync must leave Id unchanged, not re-stamp it.
        var correlationId = Guid.NewGuid();
        var (finder, mapper) = Build();
        await finder.InsertDataAsync(new IdTestData { CorrelationId = correlationId, Value = "v0" }, CancellationToken.None);

        // Capture the Id assigned at insert.
        var afterInsert = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(afterInsert);
        var originalId = ((IIdentified)afterInsert).Id;
        Assert.NotEqual(Guid.Empty, originalId);

        // First update (Version goes 1 → 2 inside UpdateDataAsync).
        afterInsert.Data.Value = "v1";
        await finder.UpdateDataAsync(afterInsert, CancellationToken.None);

        var afterFirst = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(afterFirst);
        Assert.Equal(originalId, ((IIdentified)afterFirst).Id);

        // Second update (Version goes 2 → 3 inside UpdateDataAsync).
        afterFirst.Data.Value = "v2";
        await finder.UpdateDataAsync(afterFirst, CancellationToken.None);

        var afterSecond = await finder.FindDataAsync<IdTestData>(mapper, new Message(correlationId), CancellationToken.None);
        Assert.NotNull(afterSecond);
        Assert.Equal(originalId, ((IIdentified)afterSecond).Id);
        Assert.Equal("v2", afterSecond.Data.Value);
    }
}
