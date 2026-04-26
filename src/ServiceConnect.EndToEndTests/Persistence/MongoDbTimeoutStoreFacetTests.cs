using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbTimeoutStoreFacetTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetTimeoutsBatchAsync_ReturnsDueTimeoutsFromFacet()
    {
        // Investigation for the "Uncertain" item at consolodated-issues/2026-04-22-consolidated-issues.md:
        // MongoDbTimeoutStore.GetTimeoutsBatchAsync uses `is AggregateFacetResult<TimeoutData>` pattern match on
        // the facet result. If the pinned MongoDB driver returns a non-generic carrier, the match fails silently
        // and DueTimeouts is always empty → total timeout-dispatch outage.
        //
        // This test PASSES if the pattern match works as intended (issue disconfirmed).
        // This test FAILS (DueTimeouts is empty) if the pattern match is broken (issue confirmed).

        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var dbName = _fixture.GetUniqueDatabaseName("timeoutfacet");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var store = new MongoDbTimeoutStore(
            client,
            options,
            NullLogger<MongoDbTimeoutStore>.Instance,
            timeProvider);

        var timeoutId = Guid.NewGuid();
        var processManagerId = Guid.NewGuid();
        var timeout = new TimeoutData
        {
            Id = timeoutId,
            ProcessManagerId = processManagerId,
            Destination = "test-destination",
            Time = now.AddMinutes(-5), // already due
            Locked = false,
            LockedBy = Guid.Empty,
            Headers = new Dictionary<string, object> { ["k"] = "v" },
        };

        await store.InsertTimeoutAsync(timeout);

        var batch = await store.GetTimeoutsBatchAsync();

        Assert.NotNull(batch);
        Assert.Single(batch.DueTimeouts);
        Assert.Equal(timeoutId, batch.DueTimeouts[0].Id);
        Assert.Equal(processManagerId, batch.DueTimeouts[0].ProcessManagerId);
    }
}
