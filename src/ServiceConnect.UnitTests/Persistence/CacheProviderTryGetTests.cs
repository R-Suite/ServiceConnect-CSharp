using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class CacheProviderTryGetTests
{
    [Fact]
    public void TryGet_KeyAbsent_ReturnsFalseAndDefault()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        var found = cache.TryGet<string, string>("missing", out var value);
        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public void TryGet_KeyPresent_ReturnsTrueAndValue()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        cache.Add("k", "v", CacheItemPriority.Normal);
        var found = cache.TryGet<string, string>("k", out var value);
        Assert.True(found);
        Assert.Equal("v", value);
    }

    [Fact]
    public void TryGet_KeyPresentWithNullValue_ReturnsTrueAndNull()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        cache.Add<string, object?>("k", null, CacheItemPriority.Normal);
        var found = cache.TryGet<string, object?>("k", out var value);
        Assert.True(found);
        Assert.Null(value);
    }

    [Fact]
    public void TryGet_SlidingExpiry_RefreshesOnRead()
    {
        var clock = new FakeTimeProvider();
        var cache = new CacheProvider(clock);
        cache.Add("k", "v", TimeSpan.FromSeconds(10), CacheItemPriority.Normal);

        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(cache.TryGet<string, string>("k", out _));

        // After read, sliding expiry resets — advance 8s more (16s since insert) and the key still exists.
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.True(cache.TryGet<string, string>("k", out _));
    }

    [Fact]
    public void TryGet_AfterAbsoluteExpiry_ReturnsFalse()
    {
        var clock = new FakeTimeProvider();
        var cache = new CacheProvider(clock);
        cache.Add("k", "v", TimeSpan.FromSeconds(5), CacheItemPriority.Normal);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.False(cache.TryGet<string, string>("k", out _));
    }
}
