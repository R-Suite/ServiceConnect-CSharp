using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class CacheProviderMechanicalFixesTests
{
    [Fact]
    public void Add_AbsoluteExpiryInPast_ThrowsArgumentOutOfRange()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new CacheProvider(clock);

        var pastTime = clock.GetUtcNow() - TimeSpan.FromMinutes(1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cache.Add("k", "v", pastTime));
    }

    [Fact]
    public void TryPurgeItem_AfterDispose_DoesNotThrow()
    {
        // Schedule a key with a short timeout, dispose the cache, advance the clock —
        // the timer callback path must NOT escape an ObjectDisposedException.
        // FakeTimeProvider.Advance triggers ITimer callbacks synchronously because
        // CacheProvider uses _timeProvider.CreateTimer (not raw System.Threading.Timer).
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new CacheProvider(clock);

        cache.Add("k", "v", TimeSpan.FromSeconds(1), CacheItemPriority.Normal);
        cache.Dispose();

        // Advance triggers the timer callback synchronously. With the dispose-race
        // catch in place the callback's Remove call is swallowed.
        clock.Advance(TimeSpan.FromSeconds(2));

        // No assertion needed; the test passes if no exception escapes.
    }
}
