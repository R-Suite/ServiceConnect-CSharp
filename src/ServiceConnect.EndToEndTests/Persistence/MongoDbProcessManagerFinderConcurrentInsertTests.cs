using Microsoft.Extensions.Logging.Abstractions;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.Interfaces;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.EndToEndTests;

// Startup-time index creation closes the cross-process race window where
// two cold-started processes could both insert a saga row with the same
// CorrelationId before either one ran the lazy EnsureCorrelationIdIndexAsync
// fallback. With the unique index in place before the first insert, exactly one
// concurrent insert survives and the rest fail with a duplicate-key error.
[Collection(nameof(PersistenceCollection))]
public class MongoDbProcessManagerFinderConcurrentInsertTests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    public sealed class IndexRaceSagaData : IProcessManagerData
    {
        public Guid CorrelationId { get; set; }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ConcurrentInsertSameCorrelationId_AfterStartupIndex_OnlyOneSurvives()
    {
        var dbName = _fixture.GetUniqueDatabaseName("indexrace");
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName,
        };
        var client = MongoClientFactory.Create(options);
        var finder = new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);

        // Simulate the hosted service: pre-create the unique index for the saga type.
        await finder.EnsureCorrelationIdIndexForTypeAsync(typeof(IndexRaceSagaData), CancellationToken.None);

        var correlationId = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await finder.InsertDataAsync(new IndexRaceSagaData { CorrelationId = correlationId });
                    return true;
                }
                catch (Exception)
                {
                    // Mongo throws E11000 (duplicate key) which surfaces wrapped as
                    // PersistenceException — either way, this insert lost the race.
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r);

        Assert.Equal(1, successCount);
    }
}
