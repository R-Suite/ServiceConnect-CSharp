using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

[Collection(nameof(PersistenceCollection))]
public class MongoDbAggregatorInsertOrderTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    // Concrete message type so MessageTypeRegistry.Register can map the type name
    // to the CLR type at deserialization time. Implements IHasCorrelationId so the
    // aggregator persistor can locate entries by correlation id without reflection.
    private sealed class OrderTestMessage : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public int Sequence { get; set; }
    }

    private MongoDbAggregatorPersistor BuildPersistor(string dbName, TimeProvider timeProvider, MessageTypeRegistry registry)
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
            registry,
            timeProvider);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetSnapshot_TwoInsertsAtSameTick_PreservesInsertionOrder()
    {
        // Both inserts share the same tick (clock not advanced). InsertSequence is the
        // only field that can distinguish them — without it the sort would be
        // (InsertedAtTicks=same, Id=random) which gives non-deterministic order.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var dbName = _fixture.GetUniqueDatabaseName("aggorder");
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(OrderTestMessage));
        var persistor = BuildPersistor(dbName, clock, registry);

        await persistor.InsertDataAsync(new OrderTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 1 }, "test-name", Guid.NewGuid().ToString());
        await persistor.InsertDataAsync(new OrderTestMessage { CorrelationId = Guid.NewGuid(), Sequence = 2 }, "test-name", Guid.NewGuid().ToString());

        var snapshot = await persistor.GetSnapshotAsync("test-name");
        var sequences = snapshot.ResolvedMessages.Cast<OrderTestMessage>().Select(m => m.Sequence).ToArray();

        Assert.Equal([1, 2], sequences);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task GetSnapshot_HighFrequencyInserts_PreservesInsertionOrder()
    {
        // 100 rapid inserts using the real system clock. Ticks can collide at high
        // throughput; InsertSequence provides a per-process ordering guarantee even
        // when InsertedAtTicks values are identical.
        var dbName = _fixture.GetUniqueDatabaseName("aggorder2");
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(OrderTestMessage));
        var persistor = BuildPersistor(dbName, TimeProvider.System, registry);

        for (var i = 1; i <= 100; i++)
        {
            await persistor.InsertDataAsync(new OrderTestMessage { CorrelationId = Guid.NewGuid(), Sequence = i }, "test-name", Guid.NewGuid().ToString());
        }

        var snapshot = await persistor.GetSnapshotAsync("test-name");
        var sequences = snapshot.ResolvedMessages.Cast<OrderTestMessage>().Select(m => m.Sequence).ToArray();

        Assert.Equal([.. Enumerable.Range(1, 100)], sequences);
    }
}
