using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence;

// Regression pin for the fresh-copy contract on IProcessManagerFinder.FindDataAsync<T>.
//
// ProcessManagerProcessor.UpdateData re-reads the persisted row on retry.
// If the persistor returned a cached reference, a handler that mutated
// IPersistenceData<T>.Data then threw would leak the partial mutation into
// the retry. InMemoryProcessManagerFinder must deep-clone per Find call.
file sealed class FreshCopyTestData : IProcessManagerData
{
    public Guid CorrelationId { get; set; }
    public string Name { get; set; } = "";
}

public class InMemoryProcessManagerFinderFreshDataTests
{
    [Fact]
    public async Task FindDataAsync_ReturnsFreshDataReferencePerCall()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var finder = new InMemoryProcessManagerFinder(
            new ProcessManagerPredicateCache(),
            new InMemoryPersistenceState(new FakeTimeProvider()));

        await finder.InsertDataAsync(
            new FreshCopyTestData { CorrelationId = correlationId, Name = "Original" },
            CancellationToken.None);

        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<FreshCopyTestData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);

        // Act
        var first = await finder.FindDataAsync<FreshCopyTestData>(mapper, new Message(correlationId), CancellationToken.None);
        var second = await finder.FindDataAsync<FreshCopyTestData>(mapper, new Message(correlationId), CancellationToken.None);

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(ReferenceEquals(first!.Data, second!.Data),
            "FindDataAsync must return a fresh Data instance per call so handler mutation can't leak across retries.");
    }
}
