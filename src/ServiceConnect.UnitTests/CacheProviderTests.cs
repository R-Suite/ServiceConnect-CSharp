using System;
using System.Linq;
using System.Threading;
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
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));

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
        public void Add_WithPastAbsoluteExpiry_ItemIsNotStored()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(-1));

            Assert.False(cache.Contains("key1"));
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
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));

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
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));
            object? capturedSender = null;
            cache.KeyRemoved += (sender, _) => capturedSender = sender;

            cache.Remove("key1");

            Assert.Equal("key1", capturedSender);
        }

        [Fact]
        public void Contains_ExistingKey_ReturnsTrue()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));

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
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));
            cache.Add("key2", "value2", DateTime.Now.AddMinutes(5));
            cache.Add("key3", "value3", DateTime.Now.AddMinutes(5));

            Assert.Equal(3, cache.Count());
        }

        [Fact]
        public void Count_AfterRemovingItem_Decrements()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));
            cache.Add("key2", "value2", DateTime.Now.AddMinutes(5));

            cache.Remove("key1");

            Assert.Equal(1, cache.Count());
        }

        [Fact]
        public void Clear_RemovesAllItems()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));
            cache.Add("key2", "value2", DateTime.Now.AddMinutes(5));

            cache.Clear();

            Assert.Equal(0, cache.Count());
        }

        [Fact]
        public void Keys_ReturnsAllStoredKeys()
        {
            var cache = new CacheProvider();
            cache.Add("key1", "value1", DateTime.Now.AddMinutes(5));
            cache.Add("key2", "value2", DateTime.Now.AddMinutes(5));

            var keys = cache.Keys().ToList();

            Assert.Equal(2, keys.Count);
            Assert.Contains("key1", keys);
            Assert.Contains("key2", keys);
        }

        [Fact]
        public void KeysGeneric_ReturnsOnlyKeysOfSpecifiedType()
        {
            var cache = new CacheProvider();
            cache.Add("stringKey", "value1", DateTime.Now.AddMinutes(5));
            cache.Add(42, "value2", DateTime.Now.AddMinutes(5));

            var stringKeys = cache.Keys<string>().ToList();

            Assert.Single(stringKeys);
            Assert.Equal("stringKey", stringKeys[0]);
        }

        [Fact]
        public void PurgeNormalPriorities_RemovesNormalItems_ReturnsCount()
        {
            var cache = new CacheProvider();
            cache.Add("normal1", "value1", DateTime.Now.AddMinutes(5), CacheItemPriority.Normal);
            cache.Add("normal2", "value2", DateTime.Now.AddMinutes(5), CacheItemPriority.Normal);
            cache.Add("high1", "value3", DateTime.Now.AddMinutes(5), CacheItemPriority.High);

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
        public void Default_StaticInstance_IsNotNull()
        {
            Assert.NotNull(CacheProvider.Default);
        }

        [Fact]
        public void Add_SameKeyTwice_FirstValueIsKept()
        {
            // ConcurrentDictionary.TryAdd does not overwrite existing keys
            var cache = new CacheProvider();
            cache.Add("key1", "first", DateTime.Now.AddMinutes(5));
            cache.Add("key1", "second", DateTime.Now.AddMinutes(5));

            var result = cache.Get<string, string>("key1");
            Assert.Equal("first", result);
        }

        [Fact]
        public void Add_DifferentValueTypes_RetrievableCorrectly()
        {
            var cache = new CacheProvider();
            cache.Add("int-key", 42, DateTime.Now.AddMinutes(5));
            cache.Add("bool-key", true, DateTime.Now.AddMinutes(5));

            Assert.Equal(42, cache.Get<string, int>("int-key"));
            Assert.True(cache.Get<string, bool>("bool-key"));
        }
    }
}
