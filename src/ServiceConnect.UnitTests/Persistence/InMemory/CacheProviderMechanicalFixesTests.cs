using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.InMemory;

public class CacheProviderMechanicalFixesTests
{
    [Fact]
    public async Task SlidingAdd_OldTimerCallback_DoesNotEvictNewValue()
    {
        // Drive the race deterministically with FakeTimeProvider:
        //  1. Add("k", "v1") with a 50ms sliding window — installs timer T1 with generation=1.
        //  2. Re-Add("k", "v2") with a 5s sliding window — installs T2 with generation=2.
        //  3. Advance time past 50ms. If T1's callback ever runs (timer disposal does not
        //     await callbacks under TimeProvider.System), TryPurgeItem captured generation=1
        //     while the current generation is 2 — mismatch — return without eviction.
        //  4. Cache still holds "v2".
        var fake = new FakeTimeProvider();
        var cache = new CacheProvider(fake);
        try
        {
            cache.Add("k", "v1", TimeSpan.FromMilliseconds(50));
            cache.Add("k", "v2", TimeSpan.FromMilliseconds(5000));

            fake.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(20); // let any pending timer callbacks complete

            Assert.True(cache.TryGet<string, string>("k", out var current));
            Assert.Equal("v2", current);
        }
        finally
        {
            cache.Dispose();
        }
    }
}
