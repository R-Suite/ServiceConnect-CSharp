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
public sealed class MongoDbAggregatorPersistor : IAggregatorPersistor
{
    private readonly IMongoCollection<AggregatorDocument> _collection;
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

    // Mongo returns these codes when concurrent index creation detects that an index with
    // the same keys (86) or options (85) already exists. Either way the index is present,
    // so the ensure call has succeeded as far as the caller is concerned.
    private static readonly HashSet<int> BenignIndexCodes = [85, 86];

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
    /// Creates a persistor that stores aggregator data in the specified collection.
    /// </summary>
    /// <param name="mongoClient">The MongoDB client.</param>
    /// <param name="options">The persistence options used to select the database.</param>
    /// <param name="collectionName">The collection that stores aggregator records.</param>
    /// <param name="logger">The logger used for unresolved message types.</param>
    /// <param name="typeRegistry">The registry used to resolve stored message types.</param>
    /// <param name="timeProvider">Time source used to stamp aggregator inserts.</param>
    public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, string collectionName, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;

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
    public async Task InsertDataAsync(object data, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);

            var dataType = data.GetType();
            var dataBson = data.ToBsonDocument(dataType);

            await _collection.InsertOneAsync(new AggregatorDocument
            {
                Id = Guid.NewGuid(),
                Name = name,
                DataBson = dataBson,
                // Store FullName rather than AssemblyQualifiedName so an assembly-version
                // bump between store and read doesn't invalidate the lookup.
                DataTypeName = dataType.FullName!,
                Version = 1,
                InsertedAtTicks = _timeProvider.GetUtcNow().UtcTicks,
                InsertSequence = Interlocked.Increment(ref _insertSequence),
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
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
    public async Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(name, cancellationToken).ConfigureAwait(false);
        // Preserve legacy signature: return only the resolved messages.
        return [.. snapshot.ResolvedMessages];
    }

    /// <inheritdoc />
    public async Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            // Sort by InsertedAtTicks (insertion-order), then InsertSequence (per-process
            // monotonic counter for same-tick ties), then Id as a final stable tie-break
            // for cross-process ties. Without an explicit sort MongoDB returns documents
            // in cursor order, which is not guaranteed to match insertion order.
            var sort = Builders<AggregatorDocument>.Sort
                .Ascending(x => x.InsertedAtTicks)
                .Ascending(x => x.InsertSequence)
                .Ascending(x => x.Id);  // final tie-break for cross-process ties
            var docs = await _collection.Find(filter).Sort(sort).ToListAsync(cancellationToken).ConfigureAwait(false);

            var messages = new List<object>(docs.Count);
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
                    messages.Add(BsonSerializer.Deserialize(doc.DataBson, type));
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

            return new AggregatorSnapshot(messages, ids, unresolved);
        }
        catch (BsonException ex)
        {
            throw new PersistenceException($"Failed to get aggregator data for '{name}'.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to get aggregator data for '{name}'.", ex);
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
            // Name + CorrelationId is effectively unique; DeleteOneAsync avoids a full
            // collection scan after the first match.
            var result = await _collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
            // IsAcknowledged is false under w:0 — we can't detect no-op then, so don't throw.
            // When acknowledged, DeletedCount==0 means the row wasn't there. We distinguish two
            // sub-cases: (a) no rows at all for this Name — structural mismatch, the caller used
            // the wrong aggregator name or all rows were already removed via RemoveAllAsync; and
            // (b) the Name bucket has rows but none matched this CorrelationId — a concurrency
            // race where another consumer won the delete, or the caller passed a mismatched key.
            if (result.IsAcknowledged && result.DeletedCount == 0)
            {
                var nameOnly = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
                var nameCount = await _collection.CountDocumentsAsync(nameOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (nameCount == 0)
                {
                    throw new KeyNotFoundException(
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
            // Delete only the specific documents captured in the snapshot, keyed by (Name, Id).
            // Concurrent inserts and unresolved-type records have different ids and are preserved.
            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.In(x => x.Id, snapshot.ResolvedIds));
            await _collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Ensures indexes on Name and the compound (Name, DataBson.CorrelationId) exist.
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

            await _collection.Indexes.CreateManyAsync(
                [nameIndex, nameInsertOrderIndex, nameCorrelationIndex], cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (BenignIndexCodes.Contains(ex.Code))
        {
            // Another process / thread created the same index concurrently. Their work is ours;
            // the indexes are present regardless of which side succeeded.
        }

        Volatile.Write(ref _indexed, 1);
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
    }
}
