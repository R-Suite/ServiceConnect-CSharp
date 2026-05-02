using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using MongoClientSessionHandle = MongoDB.Driver.IClientSessionHandle;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// MongoDB implementation of timeout persistence and lock-aware timeout leasing.
/// </summary>
public sealed class MongoDbTimeoutStore : ITimeoutStore
{
    private readonly IMongoClient _mongoClient;
    private readonly IMongoDatabase _mongoDatabase;
    private readonly ILogger<MongoDbTimeoutStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _batchSize;
    private readonly TimeSpan _lockLeaseDuration;

    private const string TimeoutsCollectionName = "Timeouts";

    static MongoDbTimeoutStore()
    {
        // Ensure the canonical Guid serializer is registered before any direct-ctor
        // path serialises a Guid. DI factories also call this; the static ctor covers
        // tests and custom compositions that bypass DI.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

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
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (options.TimeoutBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.TimeoutBatchSize,
                $"{nameof(MongoDbPersistenceOptions.TimeoutBatchSize)} must be positive.");
        }

        _batchSize = options.TimeoutBatchSize;
        if (options.TimeoutLockLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.TimeoutLockLeaseDuration,
                $"{nameof(MongoDbPersistenceOptions.TimeoutLockLeaseDuration)} must be positive.");
        }

        _lockLeaseDuration = options.TimeoutLockLeaseDuration;

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
        ArgumentNullException.ThrowIfNull(timeoutData);
        if (timeoutData.Id == Guid.Empty)
        {
            throw new ArgumentException("TimeoutData.Id must not be Guid.Empty.", nameof(timeoutData));
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection, cancellationToken).ConfigureAwait(false);

            await collection.InsertOneAsync(timeoutData, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to insert timeout data.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<TimeoutsBatch> GetTimeoutsBatchAsync(int? batchSize = null, CancellationToken cancellationToken = default)
    {
        if (batchSize is { } cap && cap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), cap, "batchSize must be greater than zero when supplied.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        MongoClientSessionHandle? session = null;
        try
        {
            var retval = new TimeoutsBatch { DueTimeouts = [] };
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection, cancellationToken).ConfigureAwait(false);
            var utcNow = _timeProvider.GetUtcNow();

            // Causally-consistent client session so the read of the rows we just lock-updated
            // hits a node that has applied the update (relevant under primary failover).
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
            catch (MongoException ex)
            {
                // Configuration/transient driver errors during session establishment shouldn't
                // fail the whole poll — fall back to the unsessioned path. The fallback is
                // less safe under primary failover (read-after-write lag) but better than zero.
                _logger.LogWarning(ex, "MongoDB session establishment failed; falling back to unsessioned poll.");
            }

            var sessionId = Guid.NewGuid();
            var dueUnlockedFilter = BuildDueTimeoutFilter(utcNow);
            var lockUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, true)
                .Set(x => x.LockedBy, sessionId)
                .Set(x => x.LockExpiresAt, utcNow.Add(_lockLeaseDuration));

            // Two-step claim: pull up to the effective batch cap candidate ids (UpdateMany
            // has no .Limit()), then UpdateMany filtered to those ids — still guarded by
            // the due-unlocked predicate so anything another worker raced in between is
            // silently skipped. The caller-supplied cap overrides the configured default.
            var candidateIds = await FindAsync(collection, dueUnlockedFilter,
                    Builders<TimeoutData>.Sort.Ascending(x => x.Time).Ascending(x => x.Id),
                    batchSize ?? _batchSize, session, cancellationToken)
                .ConfigureAwait(false);

            if (candidateIds.Count == 0)
            {
                return retval;
            }

            var batchFilter = dueUnlockedFilter &
                              Builders<TimeoutData>.Filter.In(x => x.Id, candidateIds);
            if (session is not null)
            {
                await collection.UpdateManyAsync(session, batchFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await collection.UpdateManyAsync(batchFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // Read back exactly the rows we just claimed (LockedBy == sessionId, lease still valid).
            // The LockExpiresAt > utcNow guard prevents a race where the lease expired between
            // the UpdateMany claim and this read-back; without it, a stale claim could return
            // rows the reaper has already unlocked and re-assigned to another worker.
            var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                            & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true)
                            & Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
            using var cursor = session is not null
                ? await collection.FindAsync(session, ownedFilter, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await collection.FindAsync(ownedFilter, cancellationToken: cancellationToken).ConfigureAwait(false);
            await cursor.ForEachAsync(retval.DueTimeouts.Add, cancellationToken).ConfigureAwait(false);

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
    public async Task RemoveDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DeleteResult result;
        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection, cancellationToken).ConfigureAwait(false);

            // null lockOwner is the unconditional id-only path — no Locked/LockedBy guard
            // so a leased row is genuinely deleted (matches the new contract; the previous
            // LockedBy == Guid.Empty filter caused silent no-op).
            FilterDefinition<TimeoutData> filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id);
            if (lockOwner is { } owner)
            {
                var utcNow = _timeProvider.GetUtcNow();
                filter &= Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                          Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, owner) &
                          Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
            }
            result = await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}'.", ex);
        }

        // Lease-checked path: zero matches means the lease has been reassigned (reaper
        // fired, or another worker re-claimed after lease expiry). The timeout is still
        // in the store and must not be treated as dispatched.
        if (lockOwner is { } expected && result.IsAcknowledged && result.DeletedCount == 0)
        {
            throw new ConcurrencyException(
                $"Lease for timeout '{id}' was invalidated; lock owner '{expected}' no longer holds the lease.");
        }
    }

    /// <inheritdoc />
    public async Task ReleaseDispatchedTimeoutAsync(Guid id, Guid? lockOwner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        UpdateResult result;
        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection, cancellationToken).ConfigureAwait(false);

            FilterDefinition<TimeoutData> filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id);
            if (lockOwner is { } owner)
            {
                var utcNow = _timeProvider.GetUtcNow();
                filter &= Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                          Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, owner) &
                          Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
            }

            var update = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            result = await collection.UpdateOneAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to release dispatched timeout with Id '{id}'.", ex);
        }

        if (lockOwner is { } expected && result.IsAcknowledged && result.MatchedCount == 0)
        {
            throw new ConcurrencyException(
                $"Lease for timeout '{id}' was invalidated; lock owner '{expected}' no longer holds the lease.");
        }
    }

    /// <summary>
    /// Clears lock fields on every row whose lease has expired, independent
    /// of the main poll. Safe to invoke from a background timer at a faster
    /// cadence than the dispatch poll — expired rows become due-unlocked
    /// immediately and the next poll (or this reaper) picks them up.
    /// </summary>
    /// <returns>The number of rows unlocked by this pass.</returns>
    public async Task<long> ReapStaleLeasesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection, cancellationToken).ConfigureAwait(false);
            var utcNow = _timeProvider.GetUtcNow();

            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Locked, true) &
                         Builders<TimeoutData>.Filter.Lte(x => x.LockExpiresAt, utcNow);
            var update = Builders<TimeoutData>.Update
                .Set(x => x.Locked, false)
                .Set(x => x.LockedBy, Guid.Empty)
                .Set(x => x.LockExpiresAt, null);
            var result = await collection.UpdateManyAsync(filter, update, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.IsAcknowledged ? result.ModifiedCount : 0L;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to reap stale timeout leases.", ex);
        }
    }

    internal static FilterDefinition<TimeoutData> BuildDueTimeoutFilter(DateTimeOffset utcNow)
    {
        var unlocked = Builders<TimeoutData>.Filter.Eq(x => x.Locked, false);
        var expiredLease = Builders<TimeoutData>.Filter.Lte(x => x.LockExpiresAt, utcNow);
        var due = Builders<TimeoutData>.Filter.Lte(x => x.Time, utcNow);
        return due & (unlocked | expiredLease);
    }

    private static async Task<List<Guid>> FindAsync(
        IMongoCollection<TimeoutData> collection,
        FilterDefinition<TimeoutData> filter,
        SortDefinition<TimeoutData> sort,
        int limit,
        MongoClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var options = new FindOptions<TimeoutData, Guid>
        {
            Sort = sort,
            Limit = limit,
            Projection = Builders<TimeoutData>.Projection.Expression(x => x.Id),
        };
        using var cursor = session is not null
            ? await collection.FindAsync(session, filter, options, cancellationToken).ConfigureAwait(false)
            : await collection.FindAsync(filter, options, cancellationToken).ConfigureAwait(false);
        return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection, CancellationToken cancellationToken)
    {
        // Every call hits MongoDB. createIndexes is idempotent server-side: if the
        // collection already has indexes matching the key spec and options, MongoDB
        // returns immediately without doing additional work. No in-process cache means
        // that if an administrator drops and recreates the database while this process is
        // running, the next write naturally recreates the indexes.
        //
        // Codes 85 (IndexOptionsConflict) and 86 (IndexKeySpecsConflict) arise when two
        // processes race to create the same index and MongoDB detects the duplicate
        // before the first creation has been fully committed. Both are swallowed so
        // multi-process startup races do not produce spurious write failures.
        //
        // Note: TimeoutData.Id maps to the MongoDB _id field (driver convention).
        // _id is always unique; creating an explicit unique index on it is rejected
        // by MongoDB with "The field 'unique' is not valid for an _id index specification".
        // The three composite indexes below are the only ones we need to create.
        //
        // Drop the legacy (Locked, Time) index from prior versions. v8 uses
        // (Time, Locked, LockExpiresAt) which covers both branches of the OR-shaped
        // due filter and the Time-prefix sort. Idempotent over IndexNotFound (code 27)
        // so fresh databases and re-runs are no-ops.
        try
        {
            await collection.Indexes.DropOneAsync("Locked_1_Time_1", cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.Code == 27)
        {
            // IndexNotFound — already dropped, or never existed.
        }

        try
        {
            var dueQueryIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.Time)
                    .Ascending(x => x.Locked)
                    .Ascending(x => x.LockExpiresAt));

            var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.LockedBy)
                    .Ascending(x => x.Locked));

            var lockExpiresAtIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys.Ascending(x => x.LockExpiresAt));

            await collection.Indexes.CreateManyAsync(
                [dueQueryIndexModel, lockedByIndexModel, lockExpiresAtIndexModel],
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.Code is 85 or 86)
        {
            // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — another process
            // created the same index concurrently. Treat as success to avoid spurious
            // first-insert failures in multi-process deployments.
        }
    }
}
