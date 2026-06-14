using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.DependencyInjection;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Options;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// End-to-end guard that an unresolvable document in the aggregator Mongo collection
/// does not block a flush of its resolvable siblings and is not deleted when the
/// flush completes, so a later release that adds the missing type can still process it.
/// </summary>
[Collection(nameof(PersistenceCollection))]
public class AggregatorUnresolvedTypeE2ETests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Aggregator_UnresolvableDocumentInCollection_ResolvableBatchDeliveredAndUnresolvedSurvives()
    {
        // Arrange
        var executed = new TaskCompletionSource<IReadOnlyList<TestMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueName = _fixture.GetUniqueQueueName("agg-unresolved");
        var dbName = _fixture.GetUniqueDatabaseName("agg-unresolved");
        const string collectionName = "Aggregator";

        // Directly inject a malformed document (unknown type) into the Mongo collection
        // so GetSnapshotAsync encounters it.
        var mongoOptions = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = dbName
        };
        var mongoClient = MongoClientFactory.Create(mongoOptions);
        var db = mongoClient.GetDatabase(dbName);
        var rawCollection = db.GetCollection<BsonDocument>(collectionName);

        // AggregatorDocument shape: Id, Name, DataBson, DataTypeName, Version
        var malformedDoc = new BsonDocument
        {
            { "_id", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) },
            { "Name", "UnresolvedAggregator" },
            { "DataBson", new BsonDocument { { "CorrelationId", new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard) } } },
            { "DataTypeName", "ServiceConnect.DoesNotExist.PhantomMessage" },
            { "Version", 1 }
        };
        await rawCollection.InsertOneAsync(malformedDoc);

        var handlerRefs = new List<HandlerReference>
        {
            new()
            {
                HandlerType = typeof(UnresolvedBatchAggregator),
                MessageType = typeof(TestMessage)
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IReadOnlyList<HandlerReference>>(handlerRefs);
        services.AddSingleton(executed);
        services.AddTransient<Aggregator<TestMessage>, UnresolvedBatchAggregator>();

        services.AddServiceConnect(builder =>
        {
            builder.UseRabbitMQ(t =>
            {
                t.Host = _fixture.RabbitMqHostname;
                t.Username = _fixture.RabbitMqUsername;
                t.Password = _fixture.RabbitMqPassword;
                t.SetClientSetting("Port", _fixture.RabbitMqPort);
                t.SetClientSetting("RetryCount", 3);
                t.SetClientSetting("RetrySeconds", 1);
                t.SslEnabled = false; // Testcontainers RabbitMQ runs plaintext
            });
            builder.ConfigureQueues(q => q.QueueName = queueName);
            builder.ConfigureBus(b => b.ScanForMessageHandlers = false);
            builder.UseMongoDbPersistence(opts =>
            {
                opts.ConnectionString = _fixture.MongoDbConnectionString;
                opts.DatabaseName = dbName;
            });
        });

        var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IBus>();
        await bus.StartConsumingAsync();

        try
        {
            // Act: publish 3 messages to an aggregator named "UnresolvedAggregator" to trigger flush
            for (var i = 0; i < 3; i++)
            {
                await bus.SendAsync(new TestMessage(Guid.NewGuid()) { Content = $"resolvable-{i}" },
                    new SendOptions { EndPoint = queueName });
            }

            // Assert: the resolvable messages are delivered to Execute
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => executed.TrySetCanceled());
            var result = await executed.Task;
            Assert.Equal(3, result.Count);

            // Assert: the malformed document is still in the collection (not deleted by flush)
            await TestPolling.WaitUntilAsync(async () =>
            {
                var filter = Builders<BsonDocument>.Filter.Eq("DataTypeName", "ServiceConnect.DoesNotExist.PhantomMessage");
                var count = await rawCollection.CountDocumentsAsync(filter);
                return count == 1;
            }, TimeSpan.FromSeconds(10));

            var phantomFilter = Builders<BsonDocument>.Filter.Eq("DataTypeName", "ServiceConnect.DoesNotExist.PhantomMessage");
            var survivingCount = await rawCollection.CountDocumentsAsync(phantomFilter);
            Assert.Equal(1, survivingCount);
        }
        finally
        {
            await bus.DisposeAsync();
            if (provider is IAsyncDisposable ap)
            {
                await ap.DisposeAsync();
            }
        }
    }
}

file class UnresolvedBatchAggregator(TaskCompletionSource<IReadOnlyList<TestMessage>> tcs) : Aggregator<TestMessage>
{
    private readonly TaskCompletionSource<IReadOnlyList<TestMessage>> _tcs = tcs;

    public override int BatchSize() => 3;
    public override TimeSpan Timeout() => TimeSpan.FromSeconds(30);
    public override Task ExecuteAsync(IReadOnlyList<TestMessage> messages, CancellationToken cancellationToken = default)
    {
        _tcs.TrySetResult(messages);
        return Task.CompletedTask;
    }
}
