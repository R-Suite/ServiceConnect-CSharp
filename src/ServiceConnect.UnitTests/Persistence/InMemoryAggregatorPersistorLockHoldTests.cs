using System;
using System.Threading.Tasks;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

/// <summary>
/// Verifies the snapshot contract after the lock-hold reduction in
/// <see cref="InMemoryAggregatorPersistor.GetSnapshotAsync"/>: entry references are
/// captured under the lock; <c>DeepClone.Clone</c> runs outside it.
/// </summary>
public class InMemoryAggregatorPersistorLockHoldTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsClonesIndependentOfStorage()
    {
        // The snapshot must hand the caller deep-cloned objects. A remove issued
        // after the snapshot is taken must not alter what that snapshot captured,
        // and must be reflected in a subsequent snapshot.
        using var persistor = new InMemoryAggregatorPersistor();
        var corrId = Guid.NewGuid();
        await persistor.InsertDataAsync(new AggregatorTestData(corrId) { Value = "original" }, "s", Guid.NewGuid().ToString());

        // Take snapshot before any remove.
        var snapshot = await persistor.GetSnapshotAsync("s");

        // Remove the entry from storage.
        await persistor.RemoveDataAsync("s", corrId);

        // Snapshot captured before the remove must still contain the entry.
        Assert.Single(snapshot.ResolvedMessages);
        Assert.Single(snapshot.ResolvedIds);

        // A subsequent snapshot taken after the remove must be empty.
        var snapshotAfter = await persistor.GetSnapshotAsync("s");
        Assert.Empty(snapshotAfter.ResolvedMessages);
    }

    [Fact]
    public async Task GetSnapshotAsync_SnapshotDataIsDeepCloned_MutationDoesNotCorruptStorage()
    {
        // Even though DeepClone now runs outside the lock, the caller must still
        // receive independent copies — mutating the returned snapshot objects must
        // not alter what subsequent reads see in storage.
        using var persistor = new InMemoryAggregatorPersistor();
        var corrId = Guid.NewGuid();
        await persistor.InsertDataAsync(
            new InMemoryAggregatorPersistorTests.AggWithNested(corrId) { Tags = { "before" } }, "s", Guid.NewGuid().ToString());

        var snapshot = await persistor.GetSnapshotAsync("s");
        var snapshotItem = Assert.IsType<InMemoryAggregatorPersistorTests.AggWithNested>(
            Assert.Single(snapshot.ResolvedMessages));

        // Mutate the snapshot object.
        snapshotItem.Tags.Add("after-snapshot-mutation");

        // Storage must be unaffected — the next snapshot should still see only "before".
        var snapshot2 = await persistor.GetSnapshotAsync("s");
        var item2 = Assert.IsType<InMemoryAggregatorPersistorTests.AggWithNested>(
            Assert.Single(snapshot2.ResolvedMessages));
        Assert.Equal(new[] { "before" }, item2.Tags);
    }
}
