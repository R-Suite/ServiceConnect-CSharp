using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// MongoDB implementation of IAggregatorPersistor.
/// Supports both standard and SSL connections via MongoDbPersistenceOptions.
/// </summary>
internal sealed class MongoDbAggregatorPersistor : IAggregatorPersistor
{
    private readonly IMongoCollection<AggregatorDocument> _collection;
    private readonly IMongoClient _mongoClient;
    private readonly ILogger<MongoDbAggregatorPersistor> _logger;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly TimeProvider _timeProvider;

    // Monotonic per-process counter: assigned to each insert via Interlocked.Increment so
    // rows that share an InsertedAtTicks value are still totally ordered within this process.
    // Cross-process ties remain unsolved (the existing Id sort is the final tie-break) but
    // per-(Name, CorrelationId) aggregator state is processed by a single consumer at a time,
    // so per-process order matches the actual usage pattern.
    private long _insertSequence;

    // Once the indexes are present we don't need to call createIndexes on every operation.
    // MongoDB's createIndexes is idempotent server-side, but the round-trip is per-message
    // on the hot path. After first success (or benign 85/86 conflict), short-circuit.
    // Non-benign errors leave the flag at 0 so the next caller retries.
    private int _indexed;

    // Serialises the first-call path so that N concurrent cold-start callers do not each
    // fire CreateManyAsync. The outer Volatile.Read fast path avoids the semaphore on every
    // subsequent call; the semaphore is only contested on cold start. Mirrors the lock used
    // in MongoDbProcessManagerFinder._indexedCollections.
    private readonly SemaphoreSlim _indexInitSemaphore = new(1, 1);

    // Mongo returns these codes when concurrent index creation detects that an index with
    // the same keys (86) or options (85) already exists. Either way the index is present,
    // so the ensure call has succeeded as far as the caller is concerned.
    private static readonly HashSet<int> BenignIndexCodes = [85, 86];

    // Lease window for a GetSnapshot/RemoveSnapshot pair. A worker that crashes mid-flush
    // holds the rows for at most this long before another worker may reclaim them; the
    // next GetSnapshotAsync's filter accepts rows whose LockExpiresAt has elapsed. Five
    // minutes is the same horizon MongoDbTimeoutStore uses for its row leases — long
    // enough that a slow but live flush will not be interrupted, short enough that a
    // crashed worker's rows are recoverable in the same operational window.
    internal static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    // The actual lease window for this instance — defaults to DefaultLeaseDuration; the
    // internal ctor overload lets E2E tests inject a short window so lease-expiry behaviour
    // can be exercised against a real broker within seconds.
    private readonly TimeSpan _leaseDuration;

    static MongoDbAggregatorPersistor()
    {
        // Ensure the canonical Guid serializer is registered before any direct-ctor
        // path serialises a Guid. DI factories also call this; the static ctor covers
        // tests and custom compositions that bypass DI.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    /// <summary>
    /// Creates a persistor that stores aggregator data in the default <c>Aggregator</c> collection.
    /// </summary>
    /// <param name="mongoClient">The MongoDB client.</param>
    /// <param name="options">The persistence options used to select the database.</param>
    /// <param name="logger">The logger used for unresolved message types.</param>
    /// <param name="typeRegistry">The registry used to resolve stored message types.</param>
    /// <param name="timeProvider">Time source used to stamp aggregator inserts.</param>
    public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry, TimeProvider? timeProvider = null)
        : this(mongoClient, options, "Aggregator", logger, typeRegistry, timeProvider)
    {
    }

    /// <summary>
    /// Test-only ctor that lets E2E tests use the default collection name with a short
    /// lease duration; production code uses the public ctors which default to
    /// <see cref="DefaultLeaseDuration"/>.
    /// </summary>
    internal MongoDbAggregatorPersistor(
        IMongoClient mongoClient,
        MongoDbPersistenceOptions options,
        ILogger<MongoDbAggregatorPersistor> logger,
        IMessageTypeRegistry typeRegistry,
        TimeProvider? timeProvider,
        TimeSpan? leaseDuration)
        : this(mongoClient, options, "Aggregator", logger, typeRegistry, timeProvider, leaseDuration)
    {
    }

    /// <summary>
    /// Creates a persistor that stores aggregator data in the specified collection.
    /// </summary>
    /// <param name="mongoClient">The MongoDB client.</param>
    /// <param name="options">The persistence options used to select the database.</param>
    /// <param name="collectionName">The collection that stores aggregator records.</param>
    /// <param name="logger">The logger used for unresolved message types.</param>
    /// <param name="typeRegistry">The registry used to resolve stored message types.</param>
    /// <param name="timeProvider">Time source used to stamp aggregator inserts.</param>
    public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, string collectionName, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry, TimeProvider? timeProvider = null)
        : this(mongoClient, options, collectionName, logger, typeRegistry, timeProvider, leaseDuration: null)
    {
    }

    /// <summary>
    /// Test-only ctor that lets callers override the lease window so lease-expiry behaviour
    /// can be exercised within seconds. Production code MUST go through the public ctors,
    /// which default to <see cref="DefaultLeaseDuration"/> — a too-short lease in production
    /// admits duplicate dispatch when a flush legitimately runs longer than the window.
    /// </summary>
    internal MongoDbAggregatorPersistor(
        IMongoClient mongoClient,
        MongoDbPersistenceOptions options,
        string collectionName,
        ILogger<MongoDbAggregatorPersistor> logger,
        IMessageTypeRegistry typeRegistry,
        TimeProvider? timeProvider,
        TimeSpan? leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentNullException.ThrowIfNull(logger);
        if (leaseDuration is { } lease && lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration),
                "Aggregator lease duration must be strictly positive.");
        }
        _mongoClient = mongoClient;
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;

        // Aggregator state is correctness-sensitive: w:0 makes RemoveDataAsync's IsAcknowledged
        // gate silently succeed, breaking the documented ConcurrencyException contract on
        // stale-version updates and allowing duplicate aggregate dispatch. Reject loudly at
        // startup, mirroring MongoDbProcessManagerFinder.
        if (!mongoClient.Settings.WriteConcern.IsAcknowledged)
        {
            throw new InvalidOperationException(
                "MongoDbAggregatorPersistor requires an acknowledged WriteConcern (w:1 or higher). " +
                "WriteConcern.Unacknowledged (w:0) breaks the IAggregatorPersistor.RemoveDataAsync " +
                "ConcurrencyException contract and allows duplicate aggregate dispatch. " +
                "Configure mongoClient.Settings.WriteConcern to a value where IsAcknowledged is true.");
        }

        try
        {
            var database = mongoClient.GetDatabase(options.DatabaseName);
            _collection = database.GetCollection<AggregatorDocument>(collectionName);
        }
        catch (BsonException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for aggregator persistence.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for aggregator persistence.", ex);
        }
    }

    /// <inheritdoc />
    public async Task InsertDataAsync(IHasCorrelationId data, string name, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var dataType = data.GetType();
            var dataBson = data.ToBsonDocument(dataType);

            // Upsert keyed on (Name, IdempotencyKey) with SetOnInsert: a re-delivery of
            // the same message lands in the update phase, finds the existing row, and
            // applies no fields (SetOnInsert is no-op on an existing match). The unique
            // partial index on (Name, IdempotencyKey) prevents two concurrent first-time
            // inserts from a clustered consumer pair both creating rows; the loser's
            // upsert raises DuplicateKey which we catch and treat as a successful no-op.
            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.Eq(x => x.IdempotencyKey, idempotencyKey));
            var insertSequence = Interlocked.Increment(ref _insertSequence);
            var update = Builders<AggregatorDocument>.Update
                .SetOnInsert(x => x.Id, Guid.NewGuid())
                .SetOnInsert(x => x.Name, name)
                .SetOnInsert(x => x.IdempotencyKey, idempotencyKey)
                .SetOnInsert(x => x.DataBson, dataBson)
                .SetOnInsert(x => x.DataTypeName, dataType.FullName ?? dataType.Name)
                .SetOnInsert(x => x.Version, 1)
                .SetOnInsert(x => x.InsertedAtTicks, _timeProvider.GetUtcNow().UtcTicks)
                .SetOnInsert(x => x.InsertSequence, insertSequence);
            try
            {
                await _collection.UpdateOneAsync(filter, update,
                    new UpdateOptions { IsUpsert = true },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                // Concurrent first-time insert by another worker for the same idempotency
                // key. The other worker won the race; their row stands and ours is the
                // intended duplicate-suppression. No-op.
            }
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to insert aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to insert aggregator data for '{name}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IHasCorrelationId>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(name, cancellationToken).ConfigureAwait(false);
        return [.. snapshot.ResolvedMessages];
    }

    /// <inheritdoc />
    public async Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default)
    {
        IClientSessionHandle? session = null;
        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            // Causally-consistent session so the read-back is guaranteed to see the lease-claim
            // write even when the primary fails over mid-call or the read-back lands on a
            // secondary that hasn't yet applied the claim. Standalone mongods don't support
            // sessions — fall back to unsessioned where StartSessionAsync throws NotSupported.
            // MongoException covers cluster-state errors that surface at session-start time
            // (e.g. no suitable server for sessions) — degrade to unsessioned rather than
            // propagating; the lease-claim filter is still correct without a session, just
            // weaker under failover.
            try
            {
                var startSessionTask = _mongoClient.StartSessionAsync(
                    new ClientSessionOptions { CausalConsistency = true },
                    cancellationToken);
                // Defensive: Mock<IMongoClient> with no StartSessionAsync setup returns null
                // (Moq's default for reference-type returns). Guard so unit tests using
                // legacy mocks aren't forced to add an explicit StartSessionAsync setup.
                if (startSessionTask is not null)
                {
                    session = await startSessionTask.ConfigureAwait(false);
                }
            }
            catch (NotSupportedException)
            {
                session = null;
            }
            catch (MongoException)
            {
                session = null;
            }

            // Atomically claim every unlocked or stale-lease row for this aggregator. The
            // filter uses $expr with $$NOW so the lease-expiry comparison evaluates against
            // mongod's clock — the client's clock cannot pull "expired" rows out from under
            // a peer holding a still-valid lease. The pipeline update writes a fresh
            // LockExpiresAt as `$$NOW + LeaseDuration`, again server-time-anchored so the
            // claim never depends on the client clock matching the server clock. Without
            // this, two workers with skewed clocks could each see a peer's lease as expired
            // and both claim the same rows — duplicate aggregator dispatch.
            var sessionId = Guid.NewGuid();
            var leaseMs = (long)_leaseDuration.TotalMilliseconds;
            var claimFilter = new BsonDocumentFilterDefinition<AggregatorDocument>(new BsonDocument
            {
                { "Name", name },
                {
                    "$expr",
                    new BsonDocument("$or", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$LockedBy", BsonNull.Value }),
                        new BsonDocument("$lte", new BsonArray { "$LockExpiresAt", "$$NOW" }),
                    })
                },
            });
            var setStage = new BsonDocument("$set", new BsonDocument
            {
                { "LockedBy", new BsonBinaryData(sessionId, GuidRepresentation.Standard) },
                {
                    "LockExpiresAt",
                    new BsonDocument("$add", new BsonArray { "$$NOW", leaseMs })
                },
            });
            var claimUpdate = new PipelineUpdateDefinition<AggregatorDocument>(
                new BsonDocumentStagePipelineDefinition<AggregatorDocument, AggregatorDocument>([setStage]));

            // Set before the await: if the UpdateMany commits server-side but the awaiter resumes
            // into a cancellation, the catch path must still attempt release. The release filter
            // is sessionId-gated so a release of an uncommitted claim is a server-side no-op.
            var leaseClaimed = true;
            List<AggregatorDocument> docs;
            try
            {
                if (session is not null)
                {
                    await _collection.UpdateManyAsync(session, claimFilter, claimUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _collection.UpdateManyAsync(claimFilter, claimUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                // Read back exactly the rows this session just claimed. The LockExpiresAt > $$NOW
                // guard (still server-anchored) rejects rows whose lease expired between the
                // claim and read in pathological clock-jump cases — same $$NOW source as above
                // so client clock skew cannot poison this leg either.
                var readBackFilter = new BsonDocumentFilterDefinition<AggregatorDocument>(new BsonDocument
                {
                    { "Name", name },
                    { "LockedBy", new BsonBinaryData(sessionId, GuidRepresentation.Standard) },
                    {
                        "$expr",
                        new BsonDocument("$gt", new BsonArray { "$LockExpiresAt", "$$NOW" })
                    },
                });
                // Sort by InsertedAtTicks (insertion-order), then InsertSequence (per-process
                // monotonic counter for same-tick ties), then Id as a final stable tie-break
                // for cross-process ties. Without an explicit sort MongoDB returns documents
                // in cursor order, which is not guaranteed to match insertion order.
                var sort = Builders<AggregatorDocument>.Sort
                    .Ascending(x => x.InsertedAtTicks)
                    .Ascending(x => x.InsertSequence)
                    .Ascending(x => x.Id);  // final tie-break for cross-process ties
                docs = session is not null
                    ? await _collection.Find(session, readBackFilter).Sort(sort).ToListAsync(cancellationToken).ConfigureAwait(false)
                    : await _collection.Find(readBackFilter).Sort(sort).ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Any failure between successful claim and successful read-back orphans the
                // lease — held by a sessionId no caller will use. Best-effort release lets the
                // next poll see the rows immediately rather than waiting on lease-expiry.
                // Release uses CancellationToken.None — the cancelling token (or a transient
                // MongoException) must not preempt cleanup. Mirrors MongoDbTimeoutStore.
                if (leaseClaimed)
                {
                    try
                    {
                        var releaseFilter = Builders<AggregatorDocument>.Filter.And(
                            Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                            Builders<AggregatorDocument>.Filter.Eq(x => x.LockedBy, sessionId));
                        var releaseUpdate = Builders<AggregatorDocument>.Update
                            .Set(x => x.LockedBy, (Guid?)null)
                            .Set(x => x.LockExpiresAt, (DateTime?)null);
                        await _collection.UpdateManyAsync(releaseFilter, releaseUpdate, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception releaseEx)
                    {
                        _logger.LogWarning(releaseEx,
                            "Best-effort aggregator lease release after error failed for session {SessionId}; lease-expiry will reclaim.",
                            sessionId);
                    }
                }
                throw;
            }

            var messages = new List<IHasCorrelationId>(docs.Count);
            var ids = new List<Guid>(docs.Count);
            var unresolved = 0;

            foreach (var doc in docs)
            {
                if (!_typeRegistry.TryResolve(doc.DataTypeName, out var type))
                {
                    _logger.LogWarning("Cannot resolve type '{TypeName}' for aggregator data", doc.DataTypeName);
                    unresolved++;
                    continue;
                }

                try
                {
                    var deserialised = BsonSerializer.Deserialize(doc.DataBson, type);
                    if (deserialised is not IHasCorrelationId withCorrId)
                    {
                        _logger.LogWarning(
                            "Aggregator document {Id} of type '{TypeName}' does not implement IHasCorrelationId; counting as unresolved",
                            doc.Id, doc.DataTypeName);
                        unresolved++;
                        continue;
                    }
                    messages.Add(withCorrId);
                    ids.Add(doc.Id);
                }
                catch (Exception ex) when (ex is BsonException or FormatException)
                {
                    // Schema drift or corrupt document for this single row — treat as unresolved
                    // so the snapshot still surfaces the rest of the aggregator's messages.
                    // BsonException covers structural BSON errors; FormatException is thrown by
                    // BsonClassMapSerializer when a field's BSON type is incompatible with the CLR
                    // property type (e.g. BsonArray stored where a string is expected).
                    _logger.LogWarning(ex,
                        "Failed to deserialise aggregator document {Id} as '{TypeName}'; counting as unresolved",
                        doc.Id, doc.DataTypeName);
                    unresolved++;
                }
            }

            return new LeasedAggregatorSnapshot
            {
                ResolvedMessages = messages,
                ResolvedIds = ids,
                UnresolvedCount = unresolved,
                LeaseSessionId = sessionId,
            };
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to get aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to get aggregator data for '{name}'.", ex);
        }
        finally
        {
            session?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.Eq("DataBson.CorrelationId", new BsonBinaryData(correlationId, GuidRepresentation.Standard))
            );
            // FindOneAndDelete returns the deleted document so we can inspect LockedBy /
            // LockExpiresAt — atomic delete-with-readback in one round-trip. Without the
            // readback, a peer holding an active lease on this row gets the row deleted out
            // from under their RemoveSnapshotAsync, which then warns "lease rotated" with no
            // useful diagnostic about who removed it. RemoveDataAsync is a public IAggregatorPersistor
            // surface that bypasses the snapshot lease by contract (callers explicitly say
            // "remove this specific row"); when a lease violation occurs we log a Warning so
            // operators can correlate the snapshot-rotation warning to the RemoveDataAsync
            // call without inferring it from timing.
            var deleted = await _collection.FindOneAndDeleteAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (deleted is not null
                && deleted.LockedBy is not null
                && (deleted.LockExpiresAt is null || deleted.LockExpiresAt > DateTime.UtcNow))
            {
                _logger.LogWarning(
                    "RemoveDataAsync deleted aggregator row for Name='{Name}', CorrelationId='{CorrelationId}' while an active lease (session={LockedBy}, expiresAt={Expires}) was held; a concurrent RemoveSnapshotAsync may surface a lease-rotation warning for the same session.",
                    name, correlationId, deleted.LockedBy, deleted.LockExpiresAt);
            }
            if (deleted is null)
            {
                // IAggregatorPersistor.RemoveDataAsync contract: throw ConcurrencyException
                // for any "row could not be located" outcome — the empty-bucket case
                // (Name has no rows) and the wrong-key case (Name has rows but none match)
                // are both shapes of "concurrent state changed under us" or "caller passed
                // a mismatched key". A separate KeyNotFoundException would break InMemory
                // parity and break callers relying on the single contracted exception type.
                var nameOnly = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
                var nameCount = await _collection.CountDocumentsAsync(nameOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (nameCount == 0)
                {
                    throw new ConcurrencyException(
                        $"Aggregator has no rows for Name='{name}'. Caller may have used the wrong " +
                        $"aggregator name or the rows were already removed (RemoveAllAsync) by another path.");
                }
                throw new ConcurrencyException(
                    $"Aggregator row not found: Name='{name}', CorrelationId='{correlationId}'. " +
                    $"{nameCount} row(s) exist for this Name but none with this CorrelationId — " +
                    $"row was concurrently removed or caller passed a mismatched key.");
            }
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to remove aggregator data for '{name}' with correlationId '{correlationId}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove aggregator data for '{name}' with correlationId '{correlationId}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAllAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            await _collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to remove all aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove all aggregator data for '{name}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task RemoveSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ResolvedIds.Count == 0)
        {
            return;
        }

        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            // Delete only the specific documents captured in the snapshot, keyed by
            // (Name, Id, LockedBy=sessionId). The session-id constraint defends against
            // a row whose lease has rotated to another worker between snapshot and
            // delete: deleting it here would clobber the new owner's claim. Snapshots
            // produced by GetSnapshotAsync always carry a session id; defensively
            // accept snapshots without one (e.g. constructed manually by a third party)
            // by falling back to the unconstrained delete.
            FilterDefinition<AggregatorDocument> filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.In(x => x.Id, snapshot.ResolvedIds));
            if (snapshot is LeasedAggregatorSnapshot leased)
            {
                filter &= Builders<AggregatorDocument>.Filter.Eq(x => x.LockedBy, leased.LeaseSessionId);
            }
            var deleteResult = await _collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
            if (deleteResult.IsAcknowledged && deleteResult.DeletedCount < snapshot.ResolvedIds.Count)
            {
                // Lease rotated to another worker mid-handler — the original handler ran
                // past its LeaseDuration and the rows were re-claimed by a peer who has
                // already dispatched. Log at Warning so operators can observe the
                // at-least-once delivery and size handler latency vs. LeaseDuration. Aggregator
                // handlers must be idempotent when handler runtime can exceed the lease.
                var sessionTag = snapshot is LeasedAggregatorSnapshot ls ? ls.LeaseSessionId.ToString() : "<no-lease>";
                _logger.LogWarning(
                    "Aggregator '{Name}' RemoveSnapshotAsync deleted {Deleted}/{Expected} rows for session {SessionId}; the lease rotated mid-dispatch and the missing rows were re-dispatched by a peer (at-least-once delivery).",
                    name, deleteResult.DeletedCount, snapshot.ResolvedIds.Count, sessionTag);
            }
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to remove snapshot aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove snapshot aggregator data for '{name}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseSnapshotAsync(string name, IAggregatorSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Only LeasedAggregatorSnapshot rows hold a server-side lease; legacy snapshot
        // shapes have no lease to release. The release filter is sessionId-gated, so
        // calling it when ResolvedIds is empty (all-unresolved batch) is a server-side
        // no-op when nothing was claimed — but correctly releases any rows that were.
        if (snapshot is not LeasedAggregatorSnapshot leased)
        {
            return;
        }

        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            // Clear LockedBy / LockExpiresAt for any row still locked under THIS snapshot's
            // session. Matching on sessionId means a row whose lease has already rotated to
            // a different worker (the at-least-once delivery scenario logged in
            // RemoveSnapshotAsync) is left untouched — that worker now owns it.
            var releaseFilter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.Eq(x => x.LockedBy, leased.LeaseSessionId));
            var releaseUpdate = Builders<AggregatorDocument>.Update
                .Set(x => x.LockedBy, (Guid?)null)
                .Set(x => x.LockExpiresAt, (DateTime?)null);
            await _collection.UpdateManyAsync(releaseFilter, releaseUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to release snapshot aggregator lease for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to release snapshot aggregator lease for '{name}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            var count = await _collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            // Clamp at int.MaxValue to match IAggregatorPersistor's int return contract. Aggregators
            // are keyed by (Name, CorrelationId) and rarely exceed a few hundred rows in normal usage;
            // the clamp guards against pathological cases without changing the contract.
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to count aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to count aggregator data for '{name}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<int> CountResolvedAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            // Snapshot the registered type-name set and use it as a Mongo $in filter on
            // DataTypeName. Documents whose CLR type can't currently be resolved are
            // excluded from the gate so unresolved-only batches don't churn the flush
            // path. The snapshot is point-in-time; a Register that runs after the
            // snapshot but before the round-trip simply lands in the next gate eval.
            var registeredTypes = _typeRegistry.AllRegisteredTypeNames();
            if (registeredTypes.Count == 0)
            {
                // No types registered means nothing can resolve — skip the round-trip.
                return 0;
            }

            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.In(x => x.DataTypeName, registeredTypes));
            var count = await _collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken).ConfigureAwait(false);
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to count resolved aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to count resolved aggregator data for '{name}'.", ex);
        }
    }

    /// <summary>
    /// Ensures the supporting indexes for this collection exist; idempotent across processes.
    /// A per-instance flag short-circuits subsequent calls after the first success or benign
    /// conflict (codes 85/86), avoiding a MongoDB round-trip on every message operation.
    /// Non-benign errors leave the flag unset so the next caller retries index creation.
    /// </summary>
    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _indexed) != 0)
        {
            return;
        }

        await _indexInitSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside the semaphore: a concurrent first-caller may have already
            // completed the create. This pattern mirrors MongoDbProcessManagerFinder's
            // _indexedCollections lock — both prevent N concurrent cold-starts each
            // firing CreateManyAsync, even though Mongo's idempotency makes it benign.
            if (Volatile.Read(ref _indexed) != 0)
            {
                return;
            }

            try
            {
                // Single-field index on Name supports GetDataAsync, RemoveAllAsync, CountAsync
                var nameIndex = new CreateIndexModel<AggregatorDocument>(
                    Builders<AggregatorDocument>.IndexKeys.Ascending(x => x.Name));

                // Compound index on (Name, InsertedAtTicks, InsertSequence) covers the sort
                // path in GetSnapshotAsync so MongoDB can satisfy the query with an index scan.
                var nameInsertOrderIndex = new CreateIndexModel<AggregatorDocument>(
                    Builders<AggregatorDocument>.IndexKeys
                        .Ascending(x => x.Name)
                        .Ascending(x => x.InsertedAtTicks)
                        .Ascending(x => x.InsertSequence));

                // Compound index on (Name, DataBson.CorrelationId) supports RemoveDataAsync.
                var nameCorrelationIndex = new CreateIndexModel<AggregatorDocument>(
                    Builders<AggregatorDocument>.IndexKeys
                        .Ascending(x => x.Name)
                        .Ascending("DataBson.CorrelationId"));

                // Unique partial index on (Name, IdempotencyKey). The partial filter
                // excludes pre-migration rows whose IdempotencyKey is missing (legacy
                // data continues to live alongside new inserts without violating the
                // constraint). New inserts always populate the key, so the unique
                // constraint catches concurrent first-time inserts from clustered
                // workers — the loser raises DuplicateKey which InsertDataAsync
                // catches and treats as a successful no-op.
                var nameIdempotencyIndex = new CreateIndexModel<AggregatorDocument>(
                    Builders<AggregatorDocument>.IndexKeys
                        .Ascending(x => x.Name)
                        .Ascending(x => x.IdempotencyKey),
                    new CreateIndexOptions<AggregatorDocument>
                    {
                        Unique = true,
                        PartialFilterExpression = Builders<AggregatorDocument>.Filter
                            .Exists(x => x.IdempotencyKey, true),
                    });

                // Compound index on (Name, LockedBy) supports the release filter in
                // ReleaseSnapshotAsync and the lease-bounded read-back filter in
                // GetSnapshotAsync. The single-field Name index narrows by aggregator,
                // but LockedBy filtering after the Name scan degrades to an in-memory
                // match at high per-Name cardinality; this compound covers the predicate
                // end-to-end. The partial filter uses $type:Binary so the index only
                // stores leased rows (Guid serialises as BinData subtype 4) and excludes
                // the vast majority of the collection where LockedBy is absent or null.
                // $type is supported in partial-index expressions since MongoDB 4.0;
                // Filter.Ne(null) serialises to { $not: { $eq: null } }, which MongoDB
                // rejects in partial-index expressions prior to 7.0. Both queries that
                // use the index filter on a non-null sessionId, so the $type exclusion
                // does not affect correctness.
                var nameLockedByIndex = new CreateIndexModel<AggregatorDocument>(
                    Builders<AggregatorDocument>.IndexKeys
                        .Ascending(x => x.Name)
                        .Ascending(x => x.LockedBy),
                    new CreateIndexOptions<AggregatorDocument>
                    {
                        PartialFilterExpression = Builders<AggregatorDocument>.Filter
                            .Type(x => x.LockedBy, BsonType.Binary),
                    });

                await _collection.Indexes.CreateManyAsync(
                    [nameIndex, nameInsertOrderIndex, nameCorrelationIndex, nameIdempotencyIndex, nameLockedByIndex], cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (BenignIndexCodes.Contains(ex.Code))
            {
                // Another process / thread created the same index concurrently. Their work is ours;
                // the indexes are present regardless of which side succeeded.
            }

            Volatile.Write(ref _indexed, 1);
        }
        finally
        {
            _indexInitSemaphore.Release();
        }
    }

    /// <summary>
    /// Internal document type for aggregator storage (not constrained by IProcessManagerData).
    /// </summary>
    [BsonIgnoreExtraElements]
    internal sealed class AggregatorDocument
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public BsonDocument DataBson { get; set; } = default!;
        public string DataTypeName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        // Stored as ticks so the field is comparable without Mongo-side
        // date handling and so legacy documents (missing the field) deserialize
        // to 0 rather than throw — 0 sorts first, preserving sensible order.
        public long InsertedAtTicks { get; set; }

        // Existing documents missing this field deserialize to 0 — same default as
        // InsertedAtTicks's introduction in a prior phase. New inserts populate via
        // Interlocked.Increment(ref _insertSequence).
        public long InsertSequence { get; set; }

        // Per-row lease columns. A non-null LockedBy + future LockExpiresAt indicates
        // a worker is mid-flush on this row; concurrent flushers see those rows as
        // unavailable and skip them. Pre-migration documents missing these fields
        // deserialize to null and look unlocked, which is the right default for any
        // row that was inserted before the lease feature shipped.
        [BsonIgnoreIfNull]
        public Guid? LockedBy { get; set; }
        [BsonIgnoreIfNull]
        public DateTime? LockExpiresAt { get; set; }

        // Stable per-message identifier (typically the broker MessageId) used by
        // InsertDataAsync's upsert to deduplicate a retry-queue redelivery while the
        // row is still buffered. Pre-migration rows have no key and never match the
        // upsert's compound filter, so legacy data is preserved without reprocessing.
        // The unique partial index ensures only documents that have an IdempotencyKey
        // participate in uniqueness; legacy null-keyed rows are excluded from the
        // constraint.
        [BsonIgnoreIfNull]
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>
    /// Snapshot type returned by <see cref="GetSnapshotAsync"/> that carries the
    /// per-call session id used to claim the rows. <see cref="RemoveSnapshotAsync"/>
    /// reads this id back so the delete only matches rows still locked under the
    /// same session — defending against the cross-process race where another worker
    /// re-claims after this session's lease expires.
    /// </summary>
    private sealed class LeasedAggregatorSnapshot : IAggregatorSnapshot
    {
        public required IReadOnlyList<IHasCorrelationId> ResolvedMessages { get; init; }
        public required IReadOnlyList<Guid> ResolvedIds { get; init; }
        public required int UnresolvedCount { get; init; }
        public required Guid LeaseSessionId { get; init; }
    }
}
