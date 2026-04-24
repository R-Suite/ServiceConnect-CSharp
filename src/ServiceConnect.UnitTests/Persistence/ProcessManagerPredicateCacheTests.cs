using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class ProcessManagerPredicateCacheTests
{
    [Fact]
    public void PredicateCacheKey_NullPropertiesHierarchy_ThrowsArgumentNullException()
    {
        // L12: Null propertiesHierarchy stored as-is silently; Equals/GetHashCode then NRE'd
        // on the hot path. Guard at construction for a clear signal.
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ProcessManagerPredicateCache.PredicateCacheKey(typeof(int), null!, typeof(string)));
        Assert.Equal("propertiesHierarchy", ex.ParamName);
    }

    [Fact]
    public void PredicateCacheKey_NullT_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ProcessManagerPredicateCache.PredicateCacheKey(
                null!,
                new Dictionary<string, Type>(),
                typeof(string)));
        Assert.Equal("t", ex.ParamName);
    }

    [Fact]
    public void PredicateCacheKey_NullPropertyType_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new ProcessManagerPredicateCache.PredicateCacheKey(
                typeof(int),
                new Dictionary<string, Type>(),
                null!));
        Assert.Equal("propertyType", ex.ParamName);
    }
}
