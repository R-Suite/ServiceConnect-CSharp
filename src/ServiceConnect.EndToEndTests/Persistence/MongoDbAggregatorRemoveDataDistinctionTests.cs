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
/// Round-trip tests confirming that RemoveDataAsync distinguishes a missing
/// Name bucket (KeyNotFoundException) from a present Name bucket where the
/// CorrelationId is unmatched (ConcurrencyException with row count).
/// </summary>
[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorRemoveDataDistinctionTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    // Named type implementing IHasCorrelationId — required since anonymous types cannot
    // implement interfaces and InsertDataAsync now enforces the contract.
    private sealed class SharedItem : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public string Value { get; set; } = "";
    }

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
    public async Task RemoveData_NoRowsForName_ThrowsConcurrencyException()
    {
        // The name has never been inserted; no documents exist for it. The contract
        // on IAggregatorPersistor.RemoveDataAsync names ConcurrencyException for any
        // "row could not be located" outcome — both the empty-bucket case and the
        // wrong-CorrelationId case — so the failure shape matches the InMemory persistor.
        var dbName = _fixture.GetUniqueDatabaseName("removedist1");
        var persistor = BuildPersistor(dbName);

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            persistor.RemoveDataAsync("never-existed", Guid.NewGuid()));
        Assert.Contains("no rows for Name", ex.Message);
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
        registry.Register(typeof(SharedItem));
        var item = new SharedItem { CorrelationId = Guid.NewGuid(), Value = "shared" };
        var persistor = BuildPersistor(dbName, registry);

        await persistor.InsertDataAsync(item, "shared-name", Guid.NewGuid().ToString());

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() =>
            persistor.RemoveDataAsync("shared-name", Guid.NewGuid()));

        Assert.Contains("row(s) exist for this Name", ex.Message);
    }
}
