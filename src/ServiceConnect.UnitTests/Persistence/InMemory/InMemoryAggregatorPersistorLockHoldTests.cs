using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.InMemory;

public class InMemoryAggregatorPersistorLockHoldTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReturnsClonedEntries()
    {
        // Lock-hold property is hard to test directly without driving real concurrency.
        // The behavioural test verifies (a) snapshots return cloned data (not the same
        // reference as stored), and (b) the post-fix structure still produces correct
        // snapshots end-to-end.
        var persistor = new InMemoryAggregatorPersistor();
        var data = new TestAggregatorMessage { CorrelationId = Guid.NewGuid(), Payload = "x" };
        await persistor.InsertDataAsync(data, "test");

        var snapshot = await persistor.GetSnapshotAsync("test");

        Assert.Single(snapshot.ResolvedMessages);
        var stored = (TestAggregatorMessage)snapshot.ResolvedMessages[0];
        Assert.Equal(data.Payload, stored.Payload);
        // Clone returned a fresh instance, not the original reference.
        Assert.NotSame(data, stored);
    }

    private sealed class TestAggregatorMessage : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public string Payload { get; set; } = string.Empty;
    }
}
