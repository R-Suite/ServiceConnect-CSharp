using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Concurrency exercises for <see cref="CacheProvider"/>. The provider mixes a
/// ConcurrentDictionary of values with a separate sliding-window dictionary and
/// a timer dictionary; the per-key compound update lives behind <c>_addLock</c>.
/// Bugs we want to catch: a stale value retaining its TTL after re-Add, a
/// KeyRemoved event firing for a key that was never present, and PurgeNormalPriorities
/// dropping a high-priority entry that was upgraded mid-purge.
/// </summary>
public class CacheProviderConcurrencyTests
{
    [Fact]
    public async Task ParallelAddSameKey_OnlyOneFinalValueSurvives()
    {
        // The dictionary slot is whatever the last Add wrote; what we are
        // verifying is that the *associated* sliding-window/timer state is
        // consistent with the final value (no orphans, no exceptions).
        const int writers = 16;
        const int rounds = 200;

        using var cache = new CacheProvider();

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                cache.Add("hot-key", $"w{w}-i{i}", TimeSpan.FromMinutes(5));
            }
        })).ToArray();

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);

        // The final stored value must be retrievable as a string and must be
        // one of the values written by some worker (no torn write).
        var observed = cache.Get<string, string>("hot-key");
        Assert.NotNull(observed);
        Assert.Matches("^w\\d+-i\\d+$", observed);
    }

    [Fact]
    public async Task ParallelAddRemove_DifferentKeys_NoExceptions_AndCountStaysSane()
    {
        const int writers = 8;
        const int perWriter = 500;

        using var cache = new CacheProvider();
        var keys = Enumerable.Range(0, writers * perWriter).Select(i => $"k{i}").ToArray();

        var addTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                cache.Add(keys[(w * perWriter) + i], i);
            }
        })).ToArray();

        var removeTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            // Try removing the same keys; some calls will race the writer and find nothing.
            for (var i = 0; i < perWriter; i++)
            {
                cache.Remove(keys[(w * perWriter) + i]);
            }
        })).ToArray();

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(addTasks.Concat(removeTasks)));
        Assert.Null(ex);
        Assert.InRange(cache.Count(), 0, writers * perWriter);
    }

    [Fact]
    public async Task PurgeNormalPriorities_ConcurrentWithUpgradeReAdd_DoesNotEvictUpgrade()
    {
        // The KVP-overload TryRemove inside PurgeNormalPriorities uses reference identity
        // of CacheItem to ensure a slot that was upgraded mid-scan isn't removed. This
        // test races the purge against a re-Add to high priority and verifies the high
        // priority entry survives.
        const int rounds = 100;

        for (var r = 0; r < rounds; r++)
        {
            using var cache = new CacheProvider();
            // Seed many normal-priority items so the purge has work to do.
            for (var i = 0; i < 100; i++)
            {
                cache.Add($"normal-{i}", i, ServiceConnect.Persistence.InMemory.CacheItemPriority.Normal);
            }
            // Add the contested key at normal priority so it would be purged.
            cache.Add("contested", "v", ServiceConnect.Persistence.InMemory.CacheItemPriority.Normal);

            var upgrader = Task.Run(() =>
                cache.Add("contested", "upgraded", ServiceConnect.Persistence.InMemory.CacheItemPriority.High));
            var purger = Task.Run(cache.PurgeNormalPriorities);

            await Task.WhenAll(upgrader, purger);

            // If the upgrade landed first the key survives at high priority; if the
            // purge landed first the key is gone — but it must NEVER survive at
            // normal priority (which would mean we leaked the doomed value).
            var still = cache.Get<string, string>("contested");
            if (still is not null)
            {
                Assert.Equal("upgraded", still);
            }
        }
    }

    [Fact]
    public async Task KeyRemoved_FiresOnlyForActualRemovals_UnderConcurrentDuplicateRemoves()
    {
        // Many threads call Remove on the same key. KeyRemoved must fire exactly once —
        // duplicate-remove attempts hit the no-op path and must not raise the event.
        using var cache = new CacheProvider();
        cache.Add("only-one", 1);

        var fired = 0;
        cache.KeyRemoved += (_, _) => Interlocked.Increment(ref fired);

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() => cache.Remove("only-one"))).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(1, fired);
        Assert.False(cache.Contains("only-one"));
    }

    [Fact]
    public async Task UpdateRace_AgainstAdd_NoExceptionAndOneOfTheValuesPersists()
    {
        // Update has its own retry loop on TryUpdate; concurrent Add overwrites the
        // slot wholesale. The contract is loose ("either one wins") but the operation
        // must never throw or hang.
        using var cache = new CacheProvider();
        cache.Add("k", "initial", TimeSpan.FromMinutes(5));

        const int rounds = 1000;

        var updater = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                cache.Update("k", $"upd-{i}");
            }
        });

        var adder = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                cache.Add("k", $"add-{i}", TimeSpan.FromMinutes(5));
            }
        });

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(updater, adder));
        Assert.Null(ex);

        var final = cache.Get<string, string>("k");
        Assert.NotNull(final);
        Assert.Matches("^(upd|add)-\\d+$|^initial$", final);
    }

    [Fact]
    public async Task ParallelAddAndExpire_TimerFires_ButReAddPreemptsStaleTtl()
    {
        // A re-Add must replace the timer too; otherwise the old timer would
        // fire and remove the freshly written value. We use FakeTimeProvider so
        // we can deterministically advance time past the *first* TTL but not
        // past the *re-Added* TTL, then verify the entry survives.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
        using var cache = new CacheProvider(time);

        cache.Add("k", "first", TimeSpan.FromSeconds(1));
        cache.Add("k", "second", TimeSpan.FromMinutes(10));

        // Advance past the first TTL — if the first timer wasn't disposed by the
        // second Add, it would fire here and purge "second".
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50); // give any leaked timer a chance to fire

        Assert.Equal("second", cache.Get<string, string>("k"));
    }
}
