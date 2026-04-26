using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

public class InMemoryAggregatorPersistorUnresolvedCountTests
{
    private sealed class Payload
    {
        public Guid CorrelationId { get; set; }
        public int X { get; set; }
    }

    [Fact]
    public async Task GetSnapshotAsync_NullDataEntries_CountedAsUnresolved()
    {
        var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
        await persistor.InsertDataAsync(new Payload { CorrelationId = Guid.NewGuid(), X = 1 }, "agg-a", CancellationToken.None);
        await persistor.InsertDataAsync(new Payload { CorrelationId = Guid.NewGuid(), X = 2 }, "agg-a", CancellationToken.None);

        // Reach into the persistor's internal store and replace one Entry with one whose
        // Data is null. This is the only way to construct an unresolved-state entry —
        // public Insert rejects null and DeepClone throws on null result, so reflection
        // is required to verify the snapshot loop counts unresolved entries correctly.
        var providerField = typeof(InMemoryAggregatorPersistor)
            .GetField("_provider", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var provider = (CacheProvider)providerField.GetValue(persistor)!;
        var rawList = provider.Get<string, object>("agg-a");
        var list = (IList)rawList;

        var entryType = list[0]!.GetType();
        var ctor = entryType.GetConstructors().Single();
        list[0] = ctor.Invoke([Guid.NewGuid(), null!]);

        var snapshot = await persistor.GetSnapshotAsync("agg-a", CancellationToken.None);

        Assert.Equal(1, snapshot.UnresolvedCount);
        Assert.Single(snapshot.ResolvedMessages);
        Assert.Single(snapshot.ResolvedIds);
    }

    [Fact]
    public async Task GetSnapshotAsync_AllResolved_UnresolvedCountIsZero()
    {
        var persistor = new InMemoryAggregatorPersistor(string.Empty, string.Empty, string.Empty);
        await persistor.InsertDataAsync(new Payload { CorrelationId = Guid.NewGuid(), X = 1 }, "agg-b", CancellationToken.None);
        await persistor.InsertDataAsync(new Payload { CorrelationId = Guid.NewGuid(), X = 2 }, "agg-b", CancellationToken.None);

        var snapshot = await persistor.GetSnapshotAsync("agg-b", CancellationToken.None);

        Assert.Equal(0, snapshot.UnresolvedCount);
        Assert.Equal(2, snapshot.ResolvedMessages.Count);
    }
}
