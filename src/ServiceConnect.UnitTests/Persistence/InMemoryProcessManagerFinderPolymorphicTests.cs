using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using ServiceConnect.UnitTests.Fakes.Messages;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

// Two unrelated saga types that share the same CorrelationId property so the
// mapper can match on either, but are not in a subtype relationship with each other.
file sealed class SagaTypeA : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public string ValueA { get; set; } = "";
}

file sealed class SagaTypeB : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public string ValueB { get; set; } = "";
}

public class InMemoryProcessManagerFinderPolymorphicTests
{
    private static IProcessManagerPropertyMapper BuildMapper()
    {
        var mapper = new TestProcessManagerPropertyMapper();
        // Map CorrelationId on both saga types so the predicate builder can compile
        // a typed accessor for whichever T FindDataAsync is called with.
        mapper.ConfigureMapping<SagaTypeA, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        mapper.ConfigureMapping<SagaTypeB, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        return mapper;
    }

    [Fact]
    public async Task FindData_StoredTypeMismatchesT_SkipsAndReturnsNull()
    {
        // Multi-saga support: a worker can host >1 saga type. InsertDataAsync wraps the
        // SagaTypeA record as MemoryData<SagaTypeA>; the InMemory flat-dictionary scan
        // for SagaTypeB MUST skip the SagaTypeA entry (no match) rather than throw.
        // The pre-v7 behaviour threw `InvalidOperationException`, which is not
        // `ConcurrencyException`, so the dispatcher had no retry path and the worker
        // surfaced permanently-failed dispatches whenever it hosted multiple saga types.
        var correlationId = Guid.NewGuid();
        var cache = new ProcessManagerPredicateCache();
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        var finder = new InMemoryProcessManagerFinder(cache, state);
        var mapper = BuildMapper();

        // Store as SagaTypeA.
        await finder.InsertDataAsync(
            new SagaTypeA { CorrelationId = correlationId, ValueA = "hello" },
            CancellationToken.None);

        // Retrieving as SagaTypeB must skip the unrelated SagaTypeA row and return null —
        // identical to the contract Mongo provides via per-saga-type collections.
        var result = await finder.FindDataAsync<SagaTypeB>(mapper, new Message(correlationId), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FindData_StoredTypeMatchesT_ReturnsMatch()
    {
        // Regression guard: inserting and retrieving with the exact same T must continue
        // to work without touching the fallback path at all.
        var correlationId = Guid.NewGuid();
        var cache = new ProcessManagerPredicateCache();
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        var finder = new InMemoryProcessManagerFinder(cache, state);
        var mapper = BuildMapper();

        await finder.InsertDataAsync(
            new SagaTypeA { CorrelationId = correlationId, ValueA = "world" },
            CancellationToken.None);

        var result = await finder.FindDataAsync<SagaTypeA>(mapper, new Message(correlationId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("world", result.Data.ValueA);
        // Version is surfaced via IVersioned; IPersistenceData<T> does not expose it directly.
        Assert.Equal(1L, ((IVersioned)result).Version);
    }
}
