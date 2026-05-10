using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.InMemory;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// CountResolvedAsync coverage for the InMemory aggregator persistor.
/// </summary>
/// <remarks>
/// The InMemory persistor cannot produce an unresolved entry by construction:
/// <see cref="InMemoryAggregatorPersistor.InsertDataAsync"/> rejects null and stores
/// typed <see cref="IHasCorrelationId"/> instances directly (no deserialise step that
/// could fail), so every record is resolved by definition. The plan's stub for
/// "InsertUnresolvedRecord" is therefore intentionally not realised here — the
/// persistor's design forecloses that branch.
///
/// The Mongo persistor is the regression backstop for unresolved-aware gating: see
/// <c>MongoDbAggregatorPersistorCountResolvedTests</c>. The smoke test below proves the
/// InMemory override is wired (CountResolvedAsync agrees with CountAsync for both
/// populated and empty buckets) so a future refactor that decouples the two won't go
/// unnoticed.
/// </remarks>
public class InMemoryAggregatorPersistorCountResolvedTests
{
    [Fact]
    public async Task CountResolvedAsync_AllInsertedRecordsAreResolved()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "agg-resolved", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "agg-resolved", Guid.NewGuid().ToString(), CancellationToken.None);
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "agg-resolved", Guid.NewGuid().ToString(), CancellationToken.None);

        var total = await persistor.CountAsync("agg-resolved");
        var resolved = await persistor.CountResolvedAsync("agg-resolved");

        Assert.Equal(3, total);
        Assert.Equal(total, resolved);
    }

    [Fact]
    public async Task CountResolvedAsync_NoBucket_ReturnsZero()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();

        var resolved = await persistor.CountResolvedAsync("missing-bucket");

        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task CountResolvedAsync_PreCancelledToken_ThrowsOCE()
    {
        IAggregatorPersistor persistor = new InMemoryAggregatorPersistor();
        await persistor.InsertDataAsync(new AggregatorTestData(Guid.NewGuid()), "agg-cancel", Guid.NewGuid().ToString(), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => persistor.CountResolvedAsync("agg-cancel", cts.Token));
    }
}
