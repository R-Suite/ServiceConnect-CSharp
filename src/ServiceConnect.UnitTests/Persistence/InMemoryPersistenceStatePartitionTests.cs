using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

public class InMemoryPersistenceStatePartitionTests
{
    [Fact]
    public void SagaProvider_IsDistinctInstance_FromPublicProvider()
    {
        using var state = new InMemoryPersistenceState(new FakeTimeProvider());
        Assert.NotSame(state.Provider, state.SagaProvider);
    }

    [Fact]
    public void UserKeysAddedToProvider_NotVisibleInSagaProvider()
    {
        using var state = new InMemoryPersistenceState(new FakeTimeProvider());
        state.Provider.Add("user/foo", new object(), CacheItemPriority.Normal);

        Assert.False(state.SagaProvider.TryGet<string, object>("user/foo", out _));
    }

    [Fact]
    public void SagaKeysAddedToSagaProvider_NotVisibleInPublicProvider()
    {
        using var state = new InMemoryPersistenceState(new FakeTimeProvider());
        state.SagaProvider.Add("saga/foo", new object(), CacheItemPriority.Normal);

        Assert.False(state.Provider.TryGet<string, object>("saga/foo", out _));
    }

    [Fact]
    public void Dispose_DisposesBothProviders()
    {
        var state = new InMemoryPersistenceState(new FakeTimeProvider());
        state.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            state.Provider.Add("k", new object(), CacheItemPriority.Normal));
        Assert.Throws<ObjectDisposedException>(() =>
            state.SagaProvider.Add("k", new object(), CacheItemPriority.Normal));
    }
}
