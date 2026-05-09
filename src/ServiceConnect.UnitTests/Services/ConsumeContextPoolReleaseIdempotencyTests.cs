using System.Reflection;
using Moq;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.UnitTests.Services;

public class ConsumeContextPoolReleaseIdempotencyTests
{
    [Fact]
    public void DoubleRelease_DoesNotPushInstanceToPoolTwice()
    {
        // A defensive double-Release must not push the same instance into _pool twice.
        // Pre-fix Release was unconditional; two Releases produced two pool entries
        // and two concurrent Rents could hand the same underlying instance to two
        // handlers (use-after-rent corruption).
        var pool = new ConsumeContextPool();
        var bus = new Mock<IBus>().Object;
        var queueConfig = new Mock<IQueueConfiguration>().Object;
        var busConfig = new Mock<IBusConfiguration>().Object;
        var headers = new Dictionary<string, object>();
        var context = pool.Rent(bus, headers, queueConfig, busConfig, null, CancellationToken.None);

        // First Release: instance returns to pool.
        context.Release();

        // Second Release: must be a no-op for pooling. The rent-token still bumps
        // (existing semantics), but _owner.Return is NOT called a second time.
        context.Release();

        // Probe the pool's internal _pool ConcurrentBag. With the idempotency guard,
        // it has exactly one entry; without the guard it would have two.
        var poolField = typeof(ConsumeContextPool).GetField("_pool",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bag = (System.Collections.Concurrent.ConcurrentBag<ConsumeContextPool.PooledConsumeContext>)poolField.GetValue(pool)!;

        Assert.Single(bag);
    }

    [Fact]
    public void RentReleaseRentReleaseCycle_PoolHasOneEntry()
    {
        // Sanity: the standard rent/release/rent/release cycle pools the same single
        // instance — _pooled is reset to 0 on Initialize so the second Release fires.
        var pool = new ConsumeContextPool();
        var bus = new Mock<IBus>().Object;
        var queueConfig = new Mock<IQueueConfiguration>().Object;
        var busConfig = new Mock<IBusConfiguration>().Object;
        var headers = new Dictionary<string, object>();

        var c1 = pool.Rent(bus, headers, queueConfig, busConfig, null, CancellationToken.None);
        c1.Release();
        var c2 = pool.Rent(bus, headers, queueConfig, busConfig, null, CancellationToken.None);
        c2.Release();

        var poolField = typeof(ConsumeContextPool).GetField("_pool",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bag = (System.Collections.Concurrent.ConcurrentBag<ConsumeContextPool.PooledConsumeContext>)poolField.GetValue(pool)!;

        Assert.Single(bag);
    }

    [Fact]
    public void StaleRentalHandle_StillThrowsAfterDoubleRelease()
    {
        // The rent-token semantics are unchanged: a stale RentalHandle reference
        // throws InvalidOperationException on access after Release, even after a
        // double-Release.
        var pool = new ConsumeContextPool();
        var bus = new Mock<IBus>().Object;
        var queueConfig = new Mock<IQueueConfiguration>().Object;
        var busConfig = new Mock<IBusConfiguration>().Object;

        var context = pool.Rent(bus, new Dictionary<string, object>(), queueConfig, busConfig, null, CancellationToken.None);
        context.Release();
        context.Release();  // double-release

        Assert.Throws<InvalidOperationException>(() => _ = context.Headers);
    }
}
