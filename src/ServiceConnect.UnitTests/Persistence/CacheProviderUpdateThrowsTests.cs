using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class CacheProviderUpdateThrowsTests
{
    [Fact]
    public void Update_KeyAbsent_ThrowsKeyNotFoundException()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        var ex = Assert.Throws<KeyNotFoundException>(() =>
            cache.Update("missing", "value"));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void Update_KeyPresent_ReplacesValueWithoutThrowing()
    {
        var cache = new CacheProvider(new FakeTimeProvider());
        cache.Add("k", "v1", CacheItemPriority.Normal);
        cache.Update("k", "v2");

        Assert.True(cache.TryGet<string, string>("k", out var value));
        Assert.Equal("v2", value);
    }
}
