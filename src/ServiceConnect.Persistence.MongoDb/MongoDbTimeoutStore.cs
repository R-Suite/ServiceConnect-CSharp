using System.Linq;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using MongoClientSessionHandle = MongoDB.Driver.IClientSessionHandle;

namespace ServiceConnect.Persistence.MongoDb;

internal sealed class NextTimeoutProjection
{
    public Guid Id { get; set; }
    public DateTimeOffset Time { get; set; }
}

/// <summary>
/// MongoDB implementation of timeout persistence and lock-aware timeout leasing.
/// </summary>
public sealed class MongoDbTimeoutStore : ITimeoutStore, ILeaseAwareTimeoutStore
{
    private readonly IMongoClient _mongoClient;
    private readonly IMongoDatabase _mongoDatabase;
    private readonly TimeProvider _timeProvider;
    private int _timeoutIndexEnsuredFlag;

    private const string TimeoutsCollectionName = "Timeouts";
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Creates a timeout store backed by MongoDB.
    /// </summary>
    /// <param name="mongoClient">The MongoDB client.</param>
    /// <param name="options">The persistence options used to select the database.</param>
    /// <param name="logger">The logger dependency required by the public API.</param>
    /// <param name="timeProvider">The time source used for lock and due-time calculations.</param>
    public MongoDbTimeoutStore(
        IMongoClient mongoClient,
        MongoDbPersistenceOptions options,
        ILogger<MongoDbTimeoutStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentNullException.ThrowIfNull(logger);
        _mongoClient = mongoClient;
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

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        MongoClientSessionHandle? session = null;
        try
        {
            var retval = new TimeoutsBatch { DueTimeouts = [] };
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);
            var utcNow = _timeProvider.GetUtcNow();

            // Use a causally-consistent client session so the aggregate facet sees the rows
            // we just claim-locked, even if a primary failover happens between the two calls.
            // Without this, a failover could route the aggregate to a secondary that hasn't
            // replicated the UpdateManyAsync yet, and we'd silently miss the rows we just locked.
            try
            {
                session = await _mongoClient.StartSessionAsync(
                    new ClientSessionOptions { CausalConsistency = true },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                // Standalone mongods / older servers don't support sessions; fall back to
                // the unsessioned path — still better than failing the whole poll.
            }

            var sessionId = Guid.NewGuid();
            var dueUnlockedFilter = BuildDueTimeoutFilter(utcNow);
            var lockUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, true)
                .Set(x => x.LockedBy, sessionId)
                .Set(x => x.LockExpiresAt, utcNow.Add(LockLeaseDuration));
            if (session is not null)
                await collection.UpdateManyAsync(session, dueUnlockedFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
            else
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

            var facetResult = session is not null
                ? await collection.Aggregate(session, facetPipeline, cancellationToken: cancellationToken)
                                   .FirstOrDefaultAsync(cancellationToken)
                                   .ConfigureAwait(false)
                : await collection.Aggregate(facetPipeline, cancellationToken: cancellationToken)
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
        finally
        {
            session?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);

            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task RemoveDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);

            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                         Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, lockOwner);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}' and lock owner '{lockOwner}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseDispatchedTimeoutAsync(Guid id, Guid lockOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);
            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                         Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, lockOwner);
            var update = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to release dispatched timeout with Id '{id}' and lock owner '{lockOwner}'.", ex);
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
        // Interlocked gate: only one thread performs index creation; the rest short-circuit
        // once the flag flips to 1. A non-atomic bool could in principle allow two threads
        // to race to CreateManyAsync and cause an IndexOptionsConflict, which we'd then
        // swallow — the atomic flag removes that spurious work entirely.
        if (Interlocked.CompareExchange(ref _timeoutIndexEnsuredFlag, 0, 0) == 1) return;

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

            await collection.Indexes.CreateManyAsync(
                [idIndexModel, lockedTimeIndexModel, lockedByIndexModel, lockExpiresAtIndexModel]
            ).ConfigureAwait(false);
            Interlocked.Exchange(ref _timeoutIndexEnsuredFlag, 1);
        }
        catch (MongoCommandException ex) when (ex.Code is 85 or 86)
        {
            // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — another process
            // created the same index concurrently. Treat as success to avoid spurious
            // first-insert failures in multi-process deployments.
            Interlocked.Exchange(ref _timeoutIndexEnsuredFlag, 1);
        }
    }
}
