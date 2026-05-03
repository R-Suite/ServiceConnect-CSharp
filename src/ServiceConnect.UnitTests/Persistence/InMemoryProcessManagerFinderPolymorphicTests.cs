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
    public async Task FindData_StoredTypeMismatchesT_ThrowsInvalidOperation()
    {
        // Arrange: insert a SagaTypeA record, then attempt to retrieve it as SagaTypeB.
        // InsertDataAsync uses the runtime type (SagaTypeA) to choose the MemoryData<>
        // wrapper, so the store holds MemoryData<SagaTypeA>.  Asking for SagaTypeB is a
        // caller error — the two types are unrelated — and must fail deterministically
        // rather than silently coercing the stored data to a different wrapper type.
        var correlationId = Guid.NewGuid();
        var cache = new ProcessManagerPredicateCache();
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        var finder = new InMemoryProcessManagerFinder(cache, state);
        var mapper = BuildMapper();

        // Store as SagaTypeA.
        await finder.InsertDataAsync(
            new SagaTypeA { CorrelationId = correlationId, ValueA = "hello" },
            CancellationToken.None);

        // Act / Assert: retrieving as SagaTypeB must throw because the stored wrapper
        // type (MemoryData<SagaTypeA>) is incompatible with the requested T (SagaTypeB).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            finder.FindDataAsync<SagaTypeB>(mapper, new Message(correlationId), CancellationToken.None));
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
    }
}
