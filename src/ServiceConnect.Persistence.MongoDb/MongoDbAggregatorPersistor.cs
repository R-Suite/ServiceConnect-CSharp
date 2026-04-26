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
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));
        _timeProvider = timeProvider ?? TimeProvider.System;

        try
        {
            var database = mongoClient.GetDatabase(options.DatabaseName);
            _collection = database.GetCollection<AggregatorDocument>(collectionName);
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
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
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
            // Sort by InsertedAtTicks (insertion-order) with Id as a stable
            // tie-break — without an explicit sort MongoDB returns documents
            // in cursor order, which is not guaranteed to match insertion
            // order (and differs between wire protocol versions).
            var sort = Builders<AggregatorDocument>.Sort
                .Ascending(x => x.InsertedAtTicks)
                .Ascending(x => x.Id);
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
            // When acknowledged, DeletedCount==0 means the row wasn't there: either a concurrent
            // removal won the race or the caller used a mismatched (name, correlationId). Mirror
            // M17's choice and raise ConcurrencyException so the caller can distinguish the race
            // from a structural persistence failure (which would have surfaced as MongoException).
            if (result.IsAcknowledged && result.DeletedCount == 0)
            {
                throw new ConcurrencyException(
                    $"Aggregator row not found: Name='{name}', CorrelationId='{correlationId}'. Row was concurrently removed or caller passed a mismatched key.");
            }
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
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to count aggregator data for '{name}'.", ex);
        }
    }

    /// <summary>
    /// Ensures indexes on Name and the compound (Name, DataBson.CorrelationId) exist.
    /// Every call hits MongoDB. createIndexes is idempotent server-side: if matching
    /// indexes already exist MongoDB returns immediately without additional work. No
    /// in-process cache means that if an administrator drops and recreates the database
    /// while this process is running, the next write naturally recreates the indexes.
    /// Codes 85 (IndexOptionsConflict) and 86 (IndexKeySpecsConflict) are swallowed to
    /// keep multi-process startup races safe.
    /// </summary>
    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Single-field index on Name supports GetDataAsync, RemoveAllAsync, CountAsync
            var nameIndex = new CreateIndexModel<AggregatorDocument>(
                Builders<AggregatorDocument>.IndexKeys.Ascending(x => x.Name));

            // Compound index on (Name, DataBson.CorrelationId) supports RemoveDataAsync.
            var nameCorrelationIndex = new CreateIndexModel<AggregatorDocument>(
                Builders<AggregatorDocument>.IndexKeys
                    .Ascending(x => x.Name)
                    .Ascending("DataBson.CorrelationId"));

            await _collection.Indexes.CreateManyAsync([nameIndex, nameCorrelationIndex], cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (BenignIndexCodes.Contains(ex.Code))
        {
            // Another process / thread created the same index concurrently. Their work is ours;
            // the indexes are present regardless of which side succeeded.
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
    }
}
