using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// Verifies the row-level lease that prevents two clustered <see cref="MongoDbAggregatorPersistor"/>
/// instances on the same Mongo collection from both snapshotting and dispatching the same rows.
/// Without the lease, both processes call <c>GetSnapshotAsync</c>, both read the full row set,
/// both invoke the aggregator's <c>Execute</c>, and both delete via <c>RemoveSnapshotAsync</c> —
/// producing a duplicate dispatch that the per-process semaphore cannot prevent.
/// </summary>
[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorLeaseTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    private sealed class LeaseTestMessage : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public int Sequence { get; set; }
    }

    private MongoDbAggregatorPersistor BuildPersistor(string dbName, TimeProvider timeProvider, MessageTypeRegistry registry)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbAggregatorPersistor(
            client,
            options,
            NullLogger<MongoDbAggregatorPersistor>.Instance,
            registry,
            timeProvider);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TwoPersistors_SecondGetSnapshotSeesNoRows_WhileFirstHoldsLease()
    {
        // Two persistors sharing one Mongo collection. After persistor A claims the rows
        // via GetSnapshotAsync, persistor B's GetSnapshotAsync must observe an empty
        // snapshot — A's lease is still valid. Without the lease both would see the
        // full row set and both would dispatch.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var dbName = _fixture.GetUniqueDatabaseName("agglease");
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(LeaseTestMessage));

        var persistorA = BuildPersistor(dbName, clock, registry);
        var persistorB = BuildPersistor(dbName, clock, registry);

        await persistorA.InsertDataAsync(new LeaseTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 1 }, "lease-test");
        await persistorA.InsertDataAsync(new LeaseTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 2 }, "lease-test");

        var snapshotA = await persistorA.GetSnapshotAsync("lease-test");
        Assert.Equal(2, snapshotA.ResolvedMessages.Count);

        var snapshotB = await persistorB.GetSnapshotAsync("lease-test");
        // B must see nothing while A holds the lease. The lease is also held under A's
        // own session id so B is forbidden from claiming until expiry.
        Assert.Empty(snapshotB.ResolvedMessages);
        Assert.Empty(snapshotB.ResolvedIds);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task LeaseExpires_SubsequentGetSnapshotReclaims()
    {
        // After the lease deadline elapses, a second persistor's GetSnapshotAsync
        // reclaims the rows. This is the recovery path for a worker that crashed or
        // was disconnected mid-flush.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var dbName = _fixture.GetUniqueDatabaseName("aggleaseexp");
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(LeaseTestMessage));

        var persistorA = BuildPersistor(dbName, clock, registry);
        var persistorB = BuildPersistor(dbName, clock, registry);

        await persistorA.InsertDataAsync(new LeaseTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 1 }, "lease-exp");

        var first = await persistorA.GetSnapshotAsync("lease-exp");
        Assert.Single(first.ResolvedMessages);

        // Advance past the 5-minute lease window.
        clock.Advance(TimeSpan.FromMinutes(6));

        var second = await persistorB.GetSnapshotAsync("lease-exp");
        Assert.Single(second.ResolvedMessages);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveSnapshot_AfterLeaseRotated_DoesNotDeleteOtherSessionsClaim()
    {
        // Edge case: A snapshots, then A's lease expires, then B claims, then A finally
        // calls RemoveSnapshotAsync. The session-scoped delete filter must NOT match B's
        // rows; otherwise A clobbers B's in-flight flush.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var dbName = _fixture.GetUniqueDatabaseName("aggleaserot");
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(LeaseTestMessage));

        var persistorA = BuildPersistor(dbName, clock, registry);
        var persistorB = BuildPersistor(dbName, clock, registry);

        await persistorA.InsertDataAsync(new LeaseTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 1 }, "lease-rot");

        var snapshotA = await persistorA.GetSnapshotAsync("lease-rot");
        Assert.Single(snapshotA.ResolvedIds);

        clock.Advance(TimeSpan.FromMinutes(6));

        var snapshotB = await persistorB.GetSnapshotAsync("lease-rot");
        Assert.Single(snapshotB.ResolvedIds);

        // A's late RemoveSnapshotAsync must observe its session id no longer matches
        // and leave B's claim intact.
        await persistorA.RemoveSnapshotAsync("lease-rot", snapshotA);

        Assert.Equal(1, await persistorB.CountAsync("lease-rot"));

        // B's RemoveSnapshotAsync clears the row normally.
        await persistorB.RemoveSnapshotAsync("lease-rot", snapshotB);
        Assert.Equal(0, await persistorB.CountAsync("lease-rot"));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetThenRemove_HappyPath_StillWorksOnSinglePersistor()
    {
        // Sanity: the single-process flow is unaffected by the lease — Get followed by
        // Remove on the same persistor still drains the rows.
        var dbName = _fixture.GetUniqueDatabaseName("aggleasehappy");
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(LeaseTestMessage));
        var persistor = BuildPersistor(dbName, TimeProvider.System, registry);

        await persistor.InsertDataAsync(new LeaseTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 1 }, "lease-happy");
        await persistor.InsertDataAsync(new LeaseTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 2 }, "lease-happy");

        var snapshot = await persistor.GetSnapshotAsync("lease-happy");
        Assert.Equal(2, snapshot.ResolvedMessages.Count);

        await persistor.RemoveSnapshotAsync("lease-happy", snapshot);
        Assert.Equal(0, await persistor.CountAsync("lease-happy"));
    }
}
