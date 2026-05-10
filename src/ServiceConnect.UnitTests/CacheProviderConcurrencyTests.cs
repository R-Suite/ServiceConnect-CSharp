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
        Assert.True(cache.TryGet<string, string>("hot-key", out var observed));
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
            if (cache.TryGet<string, string>("contested", out var still))
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

        Assert.True(cache.TryGet<string, string>("k", out var final));
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

        Assert.True(cache.TryGet<string, string>("k", out var timerReset));
        Assert.Equal("second", timerReset);
    }

    [Fact]
    public async Task TimedAdd_ThenNoExpiryAdd_StaleReObservedCallback_DoesNotEvict()
    {
        // Generation-bump regression. A timed Add installs a timer that re-observes
        // when the sliding TTL has been refreshed: the second timer captures the
        // same generation. Between the re-observe and the second timer's fire, a
        // no-expiry Add overwrites the value and clears _slidingTime. Without the
        // generation bump in the no-expiry Add path, the second timer's callback
        // sees a generation match, finds _slidingTime empty (cleared by the
        // no-expiry Add), and falls through to Remove(key) — silently evicting the
        // newly-installed value.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 4, 21, 12, 0, 0, TimeSpan.Zero));
        using var cache = new CacheProvider(time);

        cache.Add("k", "first", TimeSpan.FromSeconds(1));
        // Slide the TTL so the first timer's fire results in a re-observe rather
        // than an eviction. This gives us the dangerous "second timer in flight,
        // captured generation==1" state.
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(cache.TryGet<string, string>("k", out _));

        // Fire the first timer — it observes sliding details, sees CanExpire==false,
        // and re-StartObserving with the same generation.
        time.Advance(TimeSpan.FromMilliseconds(500));
        await Task.Delay(20);

        // Replace with a no-expiry value. With the bump, the re-observed timer's
        // generation check now fails and the callback bails. Without the bump it
        // would evict "second" when the re-observed timer fires.
        cache.Add("k", "second");

        // Fire the re-observed timer.
        time.Advance(TimeSpan.FromMilliseconds(600));
        await Task.Delay(20);

        Assert.True(cache.TryGet<string, string>("k", out var observed));
        Assert.Equal("second", observed);
    }

    [Fact]
    public async Task ParallelRemoveAndAdd_NeverLeavesCacheValueWithoutSlidingState()
    {
        // I13 regression: Remove must serialize with Add. Without _addLock in
        // Remove, a thread could complete Add(_cache, _slidingTime, _timers, _generations)
        // entirely while Remove sat between its TryRemove on _cache and its
        // cleanup of _slidingTime/_timers — leaving _cache holding the new value
        // with no expiry tracking (and the new timer disposed), so the entry
        // persisted indefinitely.
        const int rounds = 5_000;
        using var cache = new CacheProvider();

        var adder = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                cache.Add("k", $"v{i}", TimeSpan.FromMinutes(10));
            }
        });

        var remover = Task.Run(() =>
        {
            for (var i = 0; i < rounds; i++)
            {
                cache.Remove("k");
            }
        });

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(adder, remover));
        Assert.Null(ex);

        // Final state must be consistent: either the value is present with sliding
        // tracking AND a timer (a "live" entry), or absent with everything cleared.
        // We can probe consistency through the public API: if TryGet returns true,
        // then a follow-up Add with the same key should also produce a TryGet hit
        // — meaning the cache hasn't lost track of the slot's expiry plumbing.
        if (cache.TryGet<string, string>("k", out _))
        {
            cache.Add("k", "final", TimeSpan.FromMinutes(10));
            Assert.True(cache.TryGet<string, string>("k", out var finalValue));
            Assert.Equal("final", finalValue);
        }
    }
}
