using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.MongoDb;

public sealed class MongoDbTimeoutStore : ITimeoutStore
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly TimeProvider _timeProvider;
    private volatile bool _timeoutIndexEnsured;

    private const string TimeoutsCollectionName = "Timeouts";
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);

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
            var dueUnlockedFilter = Builders<TimeoutData>.Filter.Eq(x => x.Locked, false) &
                                    Builders<TimeoutData>.Filter.Lte(x => x.Time, utcNow);
            var lockUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, true)
                .Set(x => x.LockedBy, sessionId);
            var updateResult = await collection.UpdateManyAsync(dueUnlockedFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (updateResult.IsAcknowledged && updateResult.ModifiedCount > 0)
            {
                var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId) &
                                  Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
                var dueLocked = await collection.Find(ownedFilter).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var doc in dueLocked)
                    retval.DueTimeouts.Add(doc);
            }

            var nextQueryTime = DateTimeOffset.MaxValue;
            var futureFilter = Builders<TimeoutData>.Filter.Gt(x => x.Time, utcNow) &
                               Builders<TimeoutData>.Filter.Eq(x => x.Locked, false);
            var futureSort = Builders<TimeoutData>.Sort.Ascending(x => x.Time);
            var nextTimeProjection = Builders<TimeoutData>.Projection
                .Include(x => x.Id)
                .Include(x => x.Time);
            var nextTimeout = await collection.Find(futureFilter)
                .Sort(futureSort)
                .Project<TimeoutData>(nextTimeProjection)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (nextTimeout is not null)
                nextQueryTime = nextTimeout.Time;

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

            await collection.Indexes.CreateManyAsync([idIndexModel, lockedTimeIndexModel, lockedByIndexModel]).ConfigureAwait(false);
            _timeoutIndexEnsured = true;
        }
        catch
        {
            throw;
        }
    }
}
