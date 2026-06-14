using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryTimeoutStoreOptionsTests
{
    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new InMemoryTimeoutStore(options: null!));
    }

    [Theory]
    [InlineData(0)]      // TimeSpan.Zero
    [InlineData(-1000)]  // negative
    public void Constructor_NonPositiveLeaseDuration_ThrowsArgumentOutOfRange(long ticks)
    {
        var options = new InMemoryPersistenceOptions
        {
            LockLeaseDuration = TimeSpan.FromTicks(ticks),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryTimeoutStore(options));
    }

    [Fact]
    public async Task LockLeaseDuration_HonoursOptions()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new InMemoryPersistenceOptions
        {
            LockLeaseDuration = TimeSpan.FromSeconds(30),
        };
        var state = new InMemoryPersistenceState(clock);
        var store = new InMemoryTimeoutStore(options, state, clock);

        var id = Guid.NewGuid();
        await store.InsertTimeoutAsync(new TimeoutData
        {
            Id = id,
            Destination = "dest",
            ProcessManagerId = Guid.NewGuid(),
            Time = clock.GetUtcNow(),
            Headers = new Dictionary<string, object>(StringComparer.Ordinal),
        });

        // First poll: row leased.
        var first = await store.GetTimeoutsBatchAsync();
        Assert.Single(first.DueTimeouts);

        // Advance just-under the configured 30s lease — second poll returns nothing
        // because the row is still held.
        clock.Advance(TimeSpan.FromSeconds(29));
        var second = await store.GetTimeoutsBatchAsync();
        Assert.Empty(second.DueTimeouts);

        // Advance past the lease — third poll returns the row again because the
        // due-filter's expired-lease branch picks it up.
        clock.Advance(TimeSpan.FromSeconds(2));
        var third = await store.GetTimeoutsBatchAsync();
        Assert.Single(third.DueTimeouts);
    }
}
