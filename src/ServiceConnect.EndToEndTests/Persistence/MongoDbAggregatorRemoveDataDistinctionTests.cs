using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// M37: Round-trip tests confirming that RemoveDataAsync distinguishes a missing
/// Name bucket (KeyNotFoundException) from a present Name bucket where the
/// CorrelationId is unmatched (ConcurrencyException with row count).
/// </summary>
[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorRemoveDataDistinctionTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    private MongoDbAggregatorPersistor BuildPersistor(string dbName, MessageTypeRegistry? registry = null)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbAggregatorPersistor(
            client,
            options,
            NullLogger<MongoDbAggregatorPersistor>.Instance,
            registry ?? new MessageTypeRegistry());
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveData_NoRowsForName_ThrowsKeyNotFoundException()
    {
        // The name has never been inserted; no documents exist for it. The persistor
        // must raise KeyNotFoundException rather than ConcurrencyException so callers
        // can distinguish a wrong-name mistake from a concurrent-removal race.
        var dbName = _fixture.GetUniqueDatabaseName("removedist1");
        var persistor = BuildPersistor(dbName);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            persistor.RemoveDataAsync("never-existed", Guid.NewGuid()));
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task RemoveData_NameExistsButNoCorrelation_ThrowsConcurrencyExceptionWithRowCount()
    {
        // A row exists for the Name, so the bucket is non-empty, but no row carries the
        // supplied CorrelationId. This is the concurrent-removal race (or a mismatched key)
        // and must surface as ConcurrencyException with the row count in the message.
        var dbName = _fixture.GetUniqueDatabaseName("removedist2");
        var registry = new MessageTypeRegistry();
        var item = new { CorrelationId = Guid.NewGuid(), Value = "shared" };
        registry.Register(item.GetType());
        var persistor = BuildPersistor(dbName, registry);

        await persistor.InsertDataAsync(item, "shared-name");

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            persistor.RemoveDataAsync("shared-name", Guid.NewGuid()));

        Assert.Contains("row(s) exist for this Name", ex.Message);
    }
}
