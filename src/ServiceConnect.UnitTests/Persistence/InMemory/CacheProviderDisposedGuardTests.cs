using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.InMemory;

public class CacheProviderDisposedGuardTests
{
    private static CacheProvider CreateAndDispose()
    {
        var provider = new CacheProvider();
        provider.Dispose();
        return provider;
    }

    [Fact]
    public void Add_Sliding_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().Add("k", new object(), TimeSpan.FromMinutes(1)));

    [Fact]
    public void Add_Absolute_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().Add("k", new object(), DateTimeOffset.UtcNow.AddMinutes(1)));

    [Fact]
    public void Add_PriorityOnly_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().Add("k", new object()));

    [Fact]
    public void Remove_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().Remove("k"));

    [Fact]
    public void Clear_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().Clear());

    [Fact]
    public void PurgeNormalPriorities_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().PurgeNormalPriorities());

    [Fact]
    public void Update_AfterDispose_Throws() =>
        Assert.Throws<ObjectDisposedException>(() =>
            CreateAndDispose().Update("k", new object()));
}
