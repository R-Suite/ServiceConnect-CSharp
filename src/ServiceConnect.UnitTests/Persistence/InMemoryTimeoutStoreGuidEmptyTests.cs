using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryTimeoutStoreGuidEmptyTests
{
    [Fact]
    public async Task InsertTimeout_GuidEmpty_ThrowsArgumentException()
    {
        var clock = new FakeTimeProvider();
        var state = new InMemoryPersistenceState(clock);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), state, clock);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.InsertTimeoutAsync(new TimeoutData
            {
                Id = Guid.Empty,
                Destination = "dest",
                ProcessManagerId = Guid.NewGuid(),
                Time = clock.GetUtcNow(),
                Headers = new Dictionary<string, object>(StringComparer.Ordinal),
            }));
    }

    [Fact]
    public async Task InsertTimeout_ValidGuid_Succeeds()
    {
        var clock = new FakeTimeProvider();
        var state = new InMemoryPersistenceState(clock);
        var store = new InMemoryTimeoutStore(new InMemoryPersistenceOptions(), state, clock);

        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = Guid.NewGuid(),
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = clock.GetUtcNow(),
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });
        // No exception → success.
    }
}
