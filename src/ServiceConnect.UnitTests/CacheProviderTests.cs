using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests
{
    public class CacheProviderTests
    {
        [Fact]
        public void Add_WithAbsoluteExpiry_ItemIsRetrievable()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));

            var result = cache.Get<string, string>("key1");

            Assert.Equal("value1", result);
        }

        [Fact]
        public void Add_WithSlidingExpiry_ItemIsRetrievable()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", TimeSpan.FromMinutes(5));

            var result = cache.Get<string, string>("key1");

            Assert.Equal("value1", result);
        }

        [Fact]
        public void Add_WithPastAbsoluteExpiry_ThrowsArgumentOutOfRangeException()
        {
            var cache = new CacheProvider();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        [Fact]
        public void Get_WhenKeyDoesNotExist_ReturnsDefault()
        {
            var cache = new CacheProvider();

            var result = cache.Get<string, string>("nonexistent");

            Assert.Null(result);
        }

        [Fact]
        public void Get_WhenKeyDoesNotExistForValueType_ReturnsDefault()
        {
            var cache = new CacheProvider();

            var result = cache.Get<string, int>("nonexistent");

            Assert.Equal(0, result);
        }

        [Fact]
        public void Remove_ExistingKey_ItemIsRemoved()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));

            cache.Remove("key1");

            Assert.False(cache.Contains("key1"));
        }

        [Fact]
        public void Remove_NonExistentKey_DoesNotThrow()
        {
            var cache = new CacheProvider();

            var ex = Record.Exception(() => cache.Remove("nonexistent"));

            Assert.Null(ex);
        }

        [Fact]
        public void Remove_NullKey_DoesNotThrow()
        {
            var cache = new CacheProvider();

            var ex = Record.Exception(() => cache.Remove<string>(null!));

            Assert.Null(ex);
        }

        [Fact]
        public void Remove_FiresKeyRemovedEvent()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            object? capturedSender = null;
            cache.KeyRemoved += (sender, _) => capturedSender = sender;

            cache.Remove("key1");

            Assert.Equal("key1", capturedSender);
        }

        [Fact]
        public void Remove_WhenKeyAbsent_DoesNotFireKeyRemoved()
        {
            // Remove must not raise KeyRemoved when the key was not actually present,
            // otherwise subscribers would observe spurious removal events.
            var cache = new CacheProvider();
            int invocations = 0;
            cache.KeyRemoved += (_, _) => invocations++;

            cache.Remove("never-added");

            Assert.Equal(0, invocations);
        }

        [Fact]
        public void Clear_FiresKeyRemovedForEachEntry()
        {
            // Clear must raise KeyRemoved for every evicted entry so subscribers can
            // unhook per-key state; bulk removal is not allowed to be silent.
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("key2", "value2", DateTimeOffset.UtcNow.AddMinutes(5));
            var removed = new List<object?>();
            cache.KeyRemoved += (sender, _) => removed.Add(sender);

            cache.Clear();

            Assert.Equal(2, removed.Count);
            Assert.Contains("key1", removed);
            Assert.Contains("key2", removed);
        }

        [Fact]
        public void PurgeNormalPriorities_FiresKeyRemovedForEachPurgedEntry()
        {
            // PurgeNormalPriorities must raise KeyRemoved for each purged entry,
            // and only for normal-priority entries.
            var cache = new CacheProvider();
            cache.Add("normal1", "v1", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.Normal);
            cache.Add("normal2", "v2", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.Normal);
            cache.Add("high1",   "v3", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.High);
            var removed = new List<object?>();
            cache.KeyRemoved += (sender, _) => removed.Add(sender);

            var count = cache.PurgeNormalPriorities();

            Assert.Equal(2, count);
            Assert.Equal(2, removed.Count);
            Assert.Contains("normal1", removed);
            Assert.Contains("normal2", removed);
            Assert.DoesNotContain("high1", removed);
        }

        [Fact]
        public void Contains_ExistingKey_ReturnsTrue()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));

            Assert.True(cache.Contains("key1"));
        }

        [Fact]
        public void Contains_NonExistentKey_ReturnsFalse()
        {
            var cache = new CacheProvider();

            Assert.False(cache.Contains("nonexistent"));
        }

        [Fact]
        public void Count_EmptyCache_ReturnsZero()
        {
            var cache = new CacheProvider();

            Assert.Equal(0, cache.Count());
        }

        [Fact]
        public void Count_AfterAddingItems_ReturnsCorrectCount()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("key2", "value2", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("key3", "value3", DateTimeOffset.UtcNow.AddMinutes(5));

            Assert.Equal(3, cache.Count());
        }

        [Fact]
        public void Count_AfterRemovingItem_Decrements()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("key2", "value2", DateTimeOffset.UtcNow.AddMinutes(5));

            cache.Remove("key1");

            Assert.Equal(1, cache.Count());
        }

        [Fact]
        public void Clear_RemovesAllItems()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("key2", "value2", DateTimeOffset.UtcNow.AddMinutes(5));

            cache.Clear();

            Assert.Equal(0, cache.Count());
        }

        [Fact]
        public void Keys_ReturnsAllStoredKeys()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("key2", "value2", DateTimeOffset.UtcNow.AddMinutes(5));

            var keys = cache.Keys().ToList();

            Assert.Equal(2, keys.Count);
            Assert.Contains("key1", keys);
            Assert.Contains("key2", keys);
        }

        [Fact]
        public void KeysGeneric_ReturnsOnlyKeysOfSpecifiedType()
        {
            var cache = new CacheProvider();
            cache.Add("stringKey", "value1", DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add(42, "value2", DateTimeOffset.UtcNow.AddMinutes(5));

            var stringKeys = cache.Keys<string>().ToList();

            Assert.Single(stringKeys);
            Assert.Equal("stringKey", stringKeys[0]);
        }

        [Fact]
        public void PurgeNormalPriorities_RemovesNormalItems_ReturnsCount()
        {
            var cache = new CacheProvider();
            cache.Add("normal1", "value1", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.Normal);
            cache.Add("normal2", "value2", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.Normal);
            cache.Add("high1", "value3", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.High);

            int removed = cache.PurgeNormalPriorities();

            Assert.Equal(2, removed);
            Assert.True(cache.Contains("high1"));
            Assert.False(cache.Contains("normal1"));
            Assert.False(cache.Contains("normal2"));
        }

        [Fact]
        public void PurgeNormalPriorities_EmptyCache_ReturnsZero()
        {
            var cache = new CacheProvider();

            int removed = cache.PurgeNormalPriorities();

            Assert.Equal(0, removed);
        }

        [Fact]
        public async Task PurgeNormalPriorities_ConcurrentPriorityUpgrade_DoesNotRemoveUpgradedEntry()
        {
            // Bounded stress: many iterations of "purge concurrent with re-Add upgrading
            // Normal -> High" so the race between the foreach's KVP capture and the
            // TryRemove call opens repeatedly. With the key-only TryRemove, the purge's
            // scan captures a Normal CacheItem reference, the concurrent Add swaps the
            // slot to a fresh High CacheItem, and TryRemove(key) then deletes the High
            // entry it never observed. With the KVP-overload, TryRemove succeeds only
            // when the value reference still matches what the scan observed, so the
            // upgraded entry survives.

            const int iterations = 5_000;
            int losses = 0;

            for (int i = 0; i < iterations; i++)
            {
                var cache = new CacheProvider();

                // Seed several Normal entries so the foreach has multiple iterations
                // during which the concurrent upgrade can land.
                for (int k = 0; k < 16; k++)
                    cache.Add($"k{k}", $"v{k}-normal", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.Normal);

                const string victim = "k8";

                var upgrade = Task.Run(() =>
                    cache.Add(victim, "v8-high", DateTimeOffset.UtcNow.AddMinutes(5), CacheItemPriority.High));
                var purge = Task.Run(() => cache.PurgeNormalPriorities());

                await Task.WhenAll(upgrade, purge);

                // After both tasks finish, the upgrade has definitely run, so the slot
                // currently holds the High CacheItem. The High entry must survive — purge
                // is documented to remove only Normal entries.
                if (!cache.Contains(victim))
                    losses++;
            }

            Assert.Equal(0, losses);
        }

        [Fact]
        public void Add_SameKeyTwice_ReplacesValueAndResetsExpiry()
        {
            // Re-Add on an existing key must atomically replace the value and reset
            // the expiry timer so the newer window fully supersedes the previous one.
            var now = new DateTimeOffset(2026, 4, 14, 20, 0, 0, TimeSpan.Zero);
            var timeProvider = new FakeTimeProvider(now);
            var cache = new CacheProvider(timeProvider);

            // Absolute expiry so no sliding auto-refresh gets in the way of the assertion.
            cache.Add("key1", "first", now.AddMilliseconds(200));

            timeProvider.Advance(TimeSpan.FromMilliseconds(150));
            cache.Add("key1", "second", now.AddMilliseconds(500));

            // Get returns the newer value, confirming the replacement semantics.
            Assert.Equal("second", cache.Get<string, string>("key1"));

            // The first Add's +200ms timer must have been cancelled in favour of the
            // new +500ms window, so the entry survives at +300ms from t0.
            timeProvider.Advance(TimeSpan.FromMilliseconds(150));
            Assert.True(cache.Contains("key1"));

            // ...and is gone once the new window elapses.
            timeProvider.Advance(TimeSpan.FromMilliseconds(250));
            Assert.False(cache.Contains("key1"));
        }

        [Fact]
        public void Add_ThenReAddNoExpiry_ReplacesValueAndClearsExpiryState()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "first", TimeSpan.FromMinutes(5));

            cache.Add("key1", "second");

            Assert.Equal("second", cache.Get<string, string>("key1"));
            Assert.True(cache.Contains("key1"));
        }

        [Fact]
        public void Add_DifferentValueTypes_RetrievableCorrectly()
        {
            var cache = new CacheProvider();
            cache.Add("int-key", 42, DateTimeOffset.UtcNow.AddMinutes(5));
            cache.Add("bool-key", true, DateTimeOffset.UtcNow.AddMinutes(5));

            Assert.Equal(42, cache.Get<string, int>("int-key"));
            Assert.True(cache.Get<string, bool>("bool-key"));
        }

        // Timer lifecycle tests.

        [Fact]
        public void Remove_DisposesTimer_NoLeakedTimerEntry()
        {
            // After Remove, the internal _timers dictionary must not retain an entry.
            var cache = new CacheProvider();
            cache.Add("key1", "value1", TimeSpan.FromMinutes(5));

            cache.Remove("key1");

            // Verify item is gone (timer must have been cleaned up to avoid leaks).
            Assert.False(cache.Contains("key1"));
            // Disposing a cache that has already had its timers cleaned up must not throw.
            var ex = Record.Exception(() => cache.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void Clear_DisposesAllTimers_NoLeaks()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", TimeSpan.FromMinutes(5));
            cache.Add("key2", "value2", TimeSpan.FromMinutes(5));

            cache.Clear();

            Assert.Equal(0, cache.Count());
            // Subsequent Dispose must be safe (timers already cleared).
            var ex = Record.Exception(() => cache.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void PurgeNormalPriorities_CleansSlidingTimeAndTimers()
        {
            // Purging normal-priority items must also remove their _slidingTime and timer
            // entries so neither collection grows without bound.
            var cache = new CacheProvider();
            // Add with sliding expiry so SlidingDetails is created.
            cache.Add("normal1", "value1", TimeSpan.FromMinutes(5), CacheItemPriority.Normal);
            cache.Add("normal2", "value2", TimeSpan.FromMinutes(5), CacheItemPriority.Normal);
            cache.Add("high1", "value3", TimeSpan.FromMinutes(5), CacheItemPriority.High);

            int removed = cache.PurgeNormalPriorities();

            Assert.Equal(2, removed);
            Assert.True(cache.Contains("high1"));
            Assert.False(cache.Contains("normal1"));
            Assert.False(cache.Contains("normal2"));
            // Dispose must not throw — no dangling timer objects.
            var ex = Record.Exception(() => cache.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void Update_ReplacesValue_TimerUnchanged()
        {
            // Update must NOT recreate the expiry timer.
            var cache = new CacheProvider();
            cache.Add("key1", "original", TimeSpan.FromMinutes(5));

            cache.Update("key1", "updated");

            // Value is replaced.
            Assert.Equal("updated", cache.Get<string, string>("key1"));
            // Item still exists (timer not cancelled).
            Assert.True(cache.Contains("key1"));
        }

        [Fact]
        public void Update_NonExistentKey_IsNoOp()
        {
            var cache = new CacheProvider();

            var ex = Record.Exception(() => cache.Update("missing", "value"));

            Assert.Null(ex);
            Assert.False(cache.Contains("missing"));
        }

        [Fact]
        public void Update_PreservesExpiry_ItemExpiresAfterOriginalDuration()
        {
            // Confirm that Update keeps the existing timer by verifying the item
            // expires after the original short window (not reset to a new one).
            var now = new DateTimeOffset(2026, 4, 14, 20, 0, 0, TimeSpan.Zero);
            var timeProvider = new FakeTimeProvider(now);
            var cache = new CacheProvider(timeProvider);
            cache.Add("key1", "original", TimeSpan.FromMilliseconds(150));

            cache.Update("key1", "updated");

            // Value should be visible immediately.
            Assert.Equal("updated", cache.Get<string, string>("key1"));

            // After the original expiry window the item should be gone.
            timeProvider.Advance(TimeSpan.FromMilliseconds(200));
            Assert.False(cache.Contains("key1"));
        }

        [Fact]
        public void Dispose_CanBeCalledSafely_AfterClear()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", TimeSpan.FromMinutes(5));
            cache.Clear();

            // Double-dispose must not throw.
            cache.Dispose();
            var ex = Record.Exception(() => cache.Dispose());
            Assert.Null(ex);
        }

        [Fact]
        public void Remove_NonExistentKey_TimerCleanupDoesNotThrow()
        {
            var cache = new CacheProvider();

            var ex = Record.Exception(() => cache.Remove("ghost"));

            Assert.Null(ex);
        }

        [Fact]
        public void Add_WithAbsoluteExpiry_UsesProvidedTimeProviderClock()
        {
            var now = new DateTimeOffset(2026, 4, 14, 20, 0, 0, TimeSpan.Zero);
            var timeProvider = new FakeTimeProvider(now);
            var cache = new CacheProvider(timeProvider);

            cache.Add("key1", "value1", now.AddMinutes(5));
            Assert.True(cache.Contains("key1"));

            timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromMilliseconds(1)));

            Assert.False(cache.Contains("key1"));
        }

        [Fact]
        public void Add_WithSlidingExpiry_SlidesAgainstProvidedTimeProviderClock()
        {
            var now = new DateTimeOffset(2026, 4, 14, 20, 0, 0, TimeSpan.Zero);
            var timeProvider = new FakeTimeProvider(now);
            var cache = new CacheProvider(timeProvider);

            cache.Add("key1", "value1", TimeSpan.FromMinutes(5));

            timeProvider.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal("value1", cache.Get<string, string>("key1"));

            timeProvider.Advance(TimeSpan.FromMinutes(4));
            Assert.True(cache.Contains("key1"));

            timeProvider.Advance(TimeSpan.FromMinutes(2));
            Assert.False(cache.Contains("key1"));
        }

        [Fact]
        public void KeysOfObject_ReturnsAllKeysIncludingSubtypes()
        {
            // L11: Keys<TKey>() used exact-type match, so Keys<object>() returned empty because
            // no key's runtime type is literally System.Object. Expected semantic is
            // "keys assignable to TKey"; verify via object (covers everything) and IComparable
            // (Guid/string/int all implement it).
            var provider = new CacheProvider();
            provider.Add<Guid, int>(Guid.NewGuid(), 1);
            provider.Add<string, int>("two", 2);
            provider.Add<int, int>(3, 3);

            Assert.Equal(3, provider.Keys<object>().Count());
            Assert.Equal(3, provider.Keys<IComparable>().Count());
        }
    }
}
