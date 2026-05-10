using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using ServiceConnect.EndToEndTests.Fixtures;
using ServiceConnect.EndToEndTests.Helpers;
using ServiceConnect.EndToEndTests.Messages;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Persistence.MongoDb;
using ServiceConnect.Services;
using System.Collections.Concurrent;
using Xunit;

namespace ServiceConnect.EndToEndTests;

/// <summary>
/// E2E concurrency exercises for the MongoDB persistors. These run against a real
/// Mongo instance via the PersistenceFixture testcontainer so the tests actually
/// observe the driver's optimistic-concurrency semantics — a behaviour that cannot
/// be reproduced in-process. Marked Docker so they're skipped on non-Docker hosts.
/// </summary>
[Collection(nameof(PersistenceCollection))]
public class MongoDbConcurrencyE2ETests(PersistenceFixture fixture)
{
    private readonly PersistenceFixture _fixture = fixture;

    static MongoDbConcurrencyE2ETests()
    {
        // Mirror the registration the sibling MongoDbProcessManagerFinderTests do:
        // AutoMap pins the GuidSerializer at registration time and we need
        // Standard (subtype 4) so the filter lambdas line up with stored values.
        if (!BsonClassMap.IsClassMapRegistered(typeof(TestData)))
        {
            BsonClassMap.RegisterClassMap<TestData>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
            });
        }
    }

    private MongoDbProcessManagerFinder CreateFinder()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName("pmf_conc"),
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbProcessManagerFinder(client, options, NullLogger<MongoDbProcessManagerFinder>.Instance);
    }

    private MongoDbAggregatorPersistor CreateAggregator(MessageTypeRegistry registry, out string collection)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName("agg_conc"),
        };
        var client = MongoClientFactory.Create(options);
        collection = "ConcurrencyAggregator";
        return new MongoDbAggregatorPersistor(client, options, collection, NullLogger<MongoDbAggregatorPersistor>.Instance, registry);
    }

    private MongoDbTimeoutStore CreateTimeoutStore(out FakeTimeProvider time)
    {
        var now = new DateTimeOffset(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);
        time = new FakeTimeProvider(now);
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = _fixture.MongoDbConnectionString,
            DatabaseName = _fixture.GetUniqueDatabaseName("ts_conc"),
        };
        var client = MongoClientFactory.Create(options);
        return new MongoDbTimeoutStore(client, options, NullLogger<MongoDbTimeoutStore>.Instance, time);
    }

    private static IProcessManagerPropertyMapper BuildMapper()
    {
        var mapper = new TestProcessManagerPropertyMapper();
        mapper.ConfigureMapping<IProcessManagerData, Message>(m => m.CorrelationId, pm => pm.CorrelationId);
        return mapper;
    }

    // ----- Process manager finder -----

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManagerFinder_ParallelInsertSameCorrelationId_OneSucceeds_RestThrowConcurrency()
    {
        // The compound (CorrelationId, Version) unique index combined with the
        // initial-version=1 row turns concurrent first-inserts into a duplicate-key
        // error. The persistor now surfaces those as ConcurrencyException so callers
        // can re-find the just-committed row and take the update path; under a fan-in
        // race exactly one inserter wins, the rest get ConcurrencyException.
        var finder = CreateFinder();
        var corrId = Guid.NewGuid();
        const int contenders = 12;
        var successes = 0;
        var conflicts = 0;
        var unexpected = 0;

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            try
            {
                await finder.InsertDataAsync(new TestData { CorrelationId = corrId, Name = "first" });
                Interlocked.Increment(ref successes);
            }
            catch (ServiceConnect.Interfaces.Exceptions.ConcurrencyException)
            {
                Interlocked.Increment(ref conflicts);
            }
            catch
            {
                Interlocked.Increment(ref unexpected);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);
        Assert.Equal(0, unexpected);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManagerFinder_ParallelUpdate_OptimisticConcurrencyAllowsExactlyOneWinner()
    {
        // All workers pre-fetch the same v=1 snapshot, then race UpdateDataAsync.
        // The Mongo persistor's filter on (CorrelationId, Version) ensures only one
        // worker's update matches the stored version; the rest must surface
        // ConcurrencyException so the saga processor can re-read and retry.
        var finder = CreateFinder();
        var mapper = BuildMapper();
        var corrId = Guid.NewGuid();
        await finder.InsertDataAsync(new TestData { CorrelationId = corrId, Name = "init" });

        const int contenders = 12;

        // Pre-fetch the same v=1 row N times so each worker tries to update from v=1.
        var copies = new IPersistenceData<TestData>[contenders];
        for (var i = 0; i < contenders; i++)
        {
            var copy = await finder.FindDataAsync<TestData>(mapper, new Message(corrId));
            Assert.NotNull(copy);
            copy!.Data.Name = $"upd-{i}";
            copies[i] = copy;
        }

        var successes = 0;
        var conflicts = 0;

        var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
        {
            try
            {
                await finder.UpdateDataAsync(copies[i]);
                Interlocked.Increment(ref successes);
            }
            catch (ConcurrencyException)
            {
                Interlocked.Increment(ref conflicts);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);

        var final = await finder.FindDataAsync<TestData>(mapper, new Message(corrId));
        Assert.NotNull(final);
        Assert.Equal(2, ((MongoDbData<TestData>)final!).Version);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task ProcessManagerFinder_ParallelDelete_OnlyOneSucceeds_RestThrowConcurrency()
    {
        var finder = CreateFinder();
        var mapper = BuildMapper();
        var corrId = Guid.NewGuid();
        await finder.InsertDataAsync(new TestData { CorrelationId = corrId, Name = "doomed" });

        const int contenders = 12;
        var copies = new IPersistenceData<TestData>[contenders];
        for (var i = 0; i < contenders; i++)
        {
            var copy = await finder.FindDataAsync<TestData>(mapper, new Message(corrId));
            Assert.NotNull(copy);
            copies[i] = copy!;
        }

        var successes = 0;
        var conflicts = 0;

        var tasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
        {
            try
            {
                await finder.DeleteDataAsync(copies[i]);
                Interlocked.Increment(ref successes);
            }
            catch (ConcurrencyException)
            {
                Interlocked.Increment(ref conflicts);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);

        // The row is gone after the winning delete.
        Assert.Null(await finder.FindDataAsync<TestData>(mapper, new Message(corrId)));
    }

    // ----- Aggregator persistor -----

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AggregatorPersistor_ParallelInsert_AllItemsPersistedAndCounted()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(AggregatorItem));

        var persistor = CreateAggregator(registry, out _);

        const int writers = 8;
        const int perWriter = 50;
        const int expected = writers * perWriter;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await persistor.InsertDataAsync(
                    new AggregatorItem { CorrelationId = Guid.NewGuid(), Value = $"w{w}-i{i}" },
                    "shared-batch",
                    Guid.NewGuid().ToString());
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(expected, await persistor.CountAsync("shared-batch"));
        var stored = await persistor.GetDataAsync("shared-batch");
        Assert.Equal(expected, stored.Count);
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task AggregatorPersistor_ParallelRemoveSameCorrelationId_OneSucceedsRestThrowConcurrency()
    {
        var registry = new MessageTypeRegistry();
        registry.Register(typeof(AggregatorItem));

        var persistor = CreateAggregator(registry, out _);
        var item = new AggregatorItem { CorrelationId = Guid.NewGuid(), Value = "single" };
        await persistor.InsertDataAsync(item, "race-batch", Guid.NewGuid().ToString());

        const int contenders = 12;
        var successes = 0;
        var conflicts = 0;

        var tasks = Enumerable.Range(0, contenders).Select(_ => Task.Run(async () =>
        {
            try
            {
                await persistor.RemoveDataAsync("race-batch", item.CorrelationId);
                Interlocked.Increment(ref successes);
            }
            catch (ConcurrencyException)
            {
                // Rows for Name still exist but not for this CorrelationId.
                Interlocked.Increment(ref conflicts);
            }
            catch (KeyNotFoundException)
            {
                // The last row for Name was already removed by another contender.
                // From the caller's perspective this is the same race outcome — counted as a conflict.
                Interlocked.Increment(ref conflicts);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, successes);
        Assert.Equal(contenders - 1, conflicts);
        Assert.Equal(0, await persistor.CountAsync("race-batch"));
    }

    // ----- Timeout store -----

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TimeoutStore_ConcurrentGetBatch_DueRow_ClaimedByExactlyOneCaller()
    {
        // The Mongo timeout store relies on the FindOneAndUpdate atomic claim: many pollers
        // racing on the same due row must observe exactly one claimant per row, with the
        // others returning empty batches.
        var store = CreateTimeoutStore(out var time);
        var now = time.GetUtcNow();

        const int rounds = 8;
        const int parallelPolls = 6;

        for (var r = 0; r < rounds; r++)
        {
            var id = Guid.NewGuid();
            await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1 - r) });

            var observers = new ConcurrentBag<Guid>();
            var pollers = Enumerable.Range(0, parallelPolls).Select(_ => Task.Run(async () =>
            {
                var batch = await store.GetTimeoutsBatchAsync();
                foreach (var t in batch.DueTimeouts)
                {
                    if (t.Id == id)
                    {
                        observers.Add(t.LockedBy);
                    }
                }
            })).ToArray();

            await Task.WhenAll(pollers);

            Assert.Single(observers);
            Assert.NotEqual(Guid.Empty, observers.Single());

            // Move past the lease so subsequent rows can be claimed cleanly.
            time.Advance(TimeSpan.FromMinutes(10));
            await store.RemoveDispatchedTimeoutAsync(id);
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    public async Task TimeoutStore_ParallelInsertAndPoll_AllInsertedRowsObservedExactlyOnce()
    {
        // Drive the store with many concurrent inserts then drain it via successive
        // GetTimeoutsBatchAsync calls. Every inserted id must surface in exactly one
        // claimed batch — no double dispatches, no lost rows.
        var store = CreateTimeoutStore(out var time);
        var now = time.GetUtcNow();

        const int writers = 8;
        const int perWriter = 40;
        const int batchSize = 25;
        var ids = new ConcurrentBag<Guid>();

        var writerTasks = Enumerable.Range(0, writers).Select(workerIdx => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                await store.InsertTimeoutAsync(new TimeoutData { Id = id, Time = now.AddMinutes(-1) });
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);

        // Drain with a fixed batch size and without advancing time. Each batch leases its
        // chunk for the lease duration; the next batch sees the remaining unleased rows.
        // Once everything is leased, the next call returns empty.
        var seen = new HashSet<Guid>();
        var totalRows = writers * perWriter;
        var maxIterations = (totalRows / batchSize) + 5;
        for (var attempts = 0; attempts < maxIterations; attempts++)
        {
            var batch = await store.GetTimeoutsBatchAsync(batchSize);
            if (batch.DueTimeouts.Count == 0)
            {
                break;
            }
            foreach (var t in batch.DueTimeouts)
            {
                Assert.True(seen.Add(t.Id), $"Timeout {t.Id} returned twice");
            }
        }

        Assert.Equal(ids.OrderBy(g => g), seen.OrderBy(g => g));
    }

    // Implements IHasCorrelationId so the aggregator persistor can locate entries
    // by correlation id without reflection (required since the interface migration).
    private sealed class AggregatorItem : IHasCorrelationId
    {
        public Guid CorrelationId { get; set; }
        public string Value { get; set; } = "";
    }
}
