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

    // Per-instance index-creation cache. EnsureTimeoutIndexAsync is on every Insert /
    // Get / Remove / Release / Reap path; without this flag, every dispatch round-trips
    // a DropOneAsync (404 in steady state) plus a CreateManyAsync of three index specs.
    // Mirrors the saga finder's _indexedCollections + semaphore pattern (and the
    // aggregator's index init guard). Volatile.Read/Write give ordered visibility for
    // the flag without requiring Interlocked on the success path.
    private int _indexed;
    // _indexInitSemaphore is intentionally NOT Disposed: SemaphoreSlim.Dispose only
    // releases the lazily-allocated WaitHandle, and we never call AvailableWaitHandle,
    // so disposal is a functional no-op. Mirrors the saga finder precedent.
    private readonly SemaphoreSlim _indexInitSemaphore = new(1, 1);

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

        // Timeout dispatch is correctness-sensitive: w:0 makes RemoveDispatchedTimeoutAsync /
        // ReleaseDispatchedTimeoutAsync return result.IsAcknowledged==false for every call,
        // and the no-op-detection branches that gate on IsAcknowledged become silent
        // successes — so a stale-lease no-op delete looks like a successful delete and the
        // reaper hands the same row to another worker. Reject loudly at startup, mirroring
        // MongoDbProcessManagerFinder.
        if (!mongoClient.Settings.WriteConcern.IsAcknowledged)
        {
            throw new InvalidOperationException(
                "MongoDbTimeoutStore requires an acknowledged WriteConcern (w:1 or higher). " +
                "WriteConcern.Unacknowledged (w:0) makes lock-aware delete/release operations " +
                "silently no-op-succeed, allowing duplicate timeout dispatch. " +
                "Configure mongoClient.Settings.WriteConcern to a value where IsAcknowledged is true.");
        }

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
                return new TimeoutsBatch();
            }

            var batchFilter = dueUnlockedFilter &
                              Builders<TimeoutData>.Filter.In(x => x.Id, candidateIds);

            // Track whether the lease claim succeeded so the OCE handler below knows
            // whether there is anything to release. Initialised false: if UpdateMany
            // throws (or is cancelled before it starts), there is nothing to clean up.
            var leaseClaimed = false;
            var due = new List<TimeoutData>();
            try
            {
                if (session is not null)
                {
                    await collection.UpdateManyAsync(session, batchFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await collection.UpdateManyAsync(batchFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                leaseClaimed = true;

                // Read back exactly the rows we just claimed (LockedBy == sessionId, lease still valid).
                // The LockExpiresAt > utcNow guard prevents a race where the lease expired between
                // the UpdateMany claim and this read-back; without it, a stale claim could return
                // rows the reaper has already unlocked and re-assigned to another worker.
                var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                                & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true)
                                & Builders<TimeoutData>.Filter.Gt(x => x.LockExpiresAt, utcNow);
                // Sort by (Time, Id) — same shape as the candidate-id pass — so dispatch
                // order within a batch matches Time order. Without this, MongoDB's natural
                // cursor order does not respect insertion-time semantics.
                var readBackSort = Builders<TimeoutData>.Sort
                    .Ascending(x => x.Time)
                    .Ascending(x => x.Id);
                var readBackOptions = new FindOptions<TimeoutData> { Sort = readBackSort };
                using var cursor = session is not null
                    ? await collection.FindAsync(session, ownedFilter, readBackOptions, cancellationToken).ConfigureAwait(false)
                    : await collection.FindAsync(ownedFilter, readBackOptions, cancellationToken).ConfigureAwait(false);
                await cursor.ForEachAsync(due.Add, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Any failure between successful claim and successful read-back orphans the
                // lease — held by a sessionId no caller will use. Best-effort release lets the
                // next poll see the rows immediately rather than waiting on the reaper /
                // lease-expiry. Release uses CancellationToken.None — the cancelling token (or
                // a transient MongoException) must not preempt cleanup. Swallow any failure
                // here; reaper / lease-expiry is the ultimate recovery.
                if (leaseClaimed)
                {
                    try
                    {
                        var releaseFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId)
                                          & Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
                        var releaseUpdate = Builders<TimeoutData>.Update
                            .Set(x => x.Locked, false)
                            .Set(x => x.LockedBy, Guid.Empty)
                            .Set(x => x.LockExpiresAt, null);
                        await collection.UpdateManyAsync(releaseFilter, releaseUpdate, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Best-effort lease release after error failed for session {SessionId}; reaper will reclaim.", sessionId);
                    }
                }
                throw;
            }

            return new TimeoutsBatch { DueTimeouts = due };
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
        // Per-instance cache: createIndexes is idempotent server-side, but the round
        // trip on every Insert / Get / Remove / Release / Reap is wasted work and the
        // DropOneAsync below 404s every steady-state call (polluting Mongo logs).
        // Once the indexes are confirmed for this process, skip both round-trips.
        // If an administrator drops indexes mid-process the cache will not self-heal;
        // restart the process to re-run the migration. Saga finder (with its unique
        // index on CorrelationId) makes the same trade-off.
        if (Volatile.Read(ref _indexed) != 0)
        {
            return;
        }

        await _indexInitSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the semaphore so a thread that was waiting while another
            // thread completed creation does not issue redundant Drop / Create round-trips.
            if (Volatile.Read(ref _indexed) != 0)
            {
                return;
            }

            // Drop the legacy (Locked, Time) index from prior versions. The current
            // due-query shape is `Time <= utcNow AND (Locked == false OR LockExpiresAt <= utcNow)`
            // sorted by Time. A single compound (Time, Locked, LockExpiresAt) — and even
            // a 2-key (Time, LockExpiresAt) — is rejected by MongoDB with code 171
            // ("cannot index parallel arrays") because the C# driver serialises
            // DateTimeOffset as a 2-element BSON array [DateTimeTicks, OffsetMinutes]
            // and a compound index cannot span two array-typed fields. The migration is
            // idempotent over IndexNotFound (code 27) so fresh databases are no-ops.
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
                // (Time, Locked) covers the Locked == false branch of the due filter
                // with the Time-prefix sort. Time is array-valued (DateTimeOffset),
                // Locked is scalar, so this compound has no parallel arrays.
                var timeLockedIndexModel = new CreateIndexModel<TimeoutData>(
                    Builders<TimeoutData>.IndexKeys
                        .Ascending(x => x.Time)
                        .Ascending(x => x.Locked));

                var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
                    Builders<TimeoutData>.IndexKeys
                        .Ascending(x => x.LockedBy)
                        .Ascending(x => x.Locked));

                // Single-field index on LockExpiresAt covers the LockExpiresAt <= utcNow
                // branch of the OR. A single array-valued field is allowed; only
                // compounds spanning two arrays trip MongoDB's parallel-arrays rule.
                var lockExpiresAtIndexModel = new CreateIndexModel<TimeoutData>(
                    Builders<TimeoutData>.IndexKeys.Ascending(x => x.LockExpiresAt));

                await collection.Indexes.CreateManyAsync(
                    [timeLockedIndexModel, lockedByIndexModel, lockExpiresAtIndexModel],
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.Code is 85 or 86)
            {
                // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — another process
                // created the same index concurrently. Treat as success to avoid spurious
                // first-insert failures in multi-process deployments.
            }

            // Flip the cache flag ONLY after Create succeeds (or benign 85/86 conflict).
            // Any other exception (driver, network, auth) leaves _indexed == 0 so the
            // next caller retries.
            Volatile.Write(ref _indexed, 1);
        }
        finally
        {
            _indexInitSemaphore.Release();
        }
    }
}
