using System.Linq;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.MongoDb;

internal sealed class TimeoutFacetResult
{
    public List<TimeoutData> Due { get; set; } = new();
    public List<NextTimeoutProjection> Next { get; set; } = new();
}

internal sealed class NextTimeoutProjection
{
    public Guid Id { get; set; }
    public DateTimeOffset Time { get; set; }
}

public sealed class MongoDbTimeoutStore : ITimeoutStore
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly TimeProvider _timeProvider;
    private volatile bool _timeoutIndexEnsured;

    private const string TimeoutsCollectionName = "Timeouts";
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(5);

    public MongoDbTimeoutStore(
        IMongoClient mongoClient,
        MongoDbPersistenceOptions options,
        ILogger<MongoDbTimeoutStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider ?? TimeProvider.System;

        try
        {
            _mongoDatabase = mongoClient.GetDatabase(options.DatabaseName);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for timeout persistence.", ex);
        }
    }

    public async Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);

            await collection.InsertOneAsync(timeoutData, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to insert timeout data.", ex);
        }
    }

    public async Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var retval = new TimeoutsBatch { DueTimeouts = [] };
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var utcNow = _timeProvider.GetUtcNow();

            var sessionId = Guid.NewGuid();
            var dueUnlockedFilter = BuildDueTimeoutFilter(utcNow);
            var lockUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, true)
                .Set(x => x.LockedBy, sessionId)
                .Set(x => x.LockExpiresAt, utcNow.Add(LockLeaseDuration));
            await collection.UpdateManyAsync(dueUnlockedFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);

            var duePipeline = new EmptyPipelineDefinition<TimeoutData>()
                .Match(t => t.LockedBy == sessionId && t.Locked);

            var nextPipeline = new EmptyPipelineDefinition<TimeoutData>()
                .Match(t => t.Time > utcNow && !t.Locked)
                .Sort(Builders<TimeoutData>.Sort.Ascending(t => t.Time))
                .Limit(1)
                .Project(t => new NextTimeoutProjection { Id = t.Id, Time = t.Time });

            var facetPipeline = new EmptyPipelineDefinition<TimeoutData>()
                .Facet(
                    AggregateFacet.Create("Due", duePipeline),
                    AggregateFacet.Create("Next", nextPipeline));

            var facetResult = await collection.Aggregate(facetPipeline, cancellationToken: cancellationToken)
                                              .FirstOrDefaultAsync(cancellationToken)
                                              .ConfigureAwait(false);

            var nextQueryTime = DateTimeOffset.MaxValue;
            if (facetResult is not null)
            {
                var dueFacet = facetResult.Facets.FirstOrDefault(f => f.Name == "Due");
                if (dueFacet is AggregateFacetResult<TimeoutData> typedDue)
                {
                    foreach (var doc in typedDue.Output)
                        retval.DueTimeouts.Add(doc);
                }

                var nextFacet = facetResult.Facets.FirstOrDefault(f => f.Name == "Next");
                if (nextFacet is AggregateFacetResult<NextTimeoutProjection> typedNext
                    && typedNext.Output.Count > 0)
                {
                    nextQueryTime = typedNext.Output[0].Time;
                }
            }

            if (nextQueryTime == DateTimeOffset.MaxValue)
                nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

            retval.NextQueryTime = nextQueryTime;
            return retval;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to get timeouts batch.", ex);
        }
    }

    public async Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}'.", ex);
        }
    }

    public async Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            var update = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to release dispatched timeout with Id '{id}'.", ex);
        }
    }

    internal static FilterDefinition<TimeoutData> BuildDueTimeoutFilter(DateTimeOffset utcNow)
    {
        var unlocked = Builders<TimeoutData>.Filter.Eq(x => x.Locked, false);
        var expiredLease = Builders<TimeoutData>.Filter.Lte(x => x.LockExpiresAt, utcNow);
        var due = Builders<TimeoutData>.Filter.Lte(x => x.Time, utcNow);
        return due & (unlocked | expiredLease);
    }

    private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection)
    {
        if (_timeoutIndexEnsured) return;

        try
        {
            var idIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys.Ascending(x => x.Id));

            var lockedTimeIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.Locked)
                    .Ascending(x => x.Time));

            var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.LockedBy)
                    .Ascending(x => x.Locked));

            var lockExpiresAtIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys.Ascending(x => x.LockExpiresAt));

            await collection.Indexes.CreateManyAsync([idIndexModel, lockedTimeIndexModel, lockedByIndexModel, lockExpiresAtIndexModel]).ConfigureAwait(false);
            _timeoutIndexEnsured = true;
        }
        catch
        {
            throw;
        }
    }
}
