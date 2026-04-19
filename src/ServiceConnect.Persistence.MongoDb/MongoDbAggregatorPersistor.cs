using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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
    private volatile bool _indexesEnsured;

    /// <summary>
    /// Creates a persistor that stores aggregator data in the default <c>Aggregator</c> collection.
    /// </summary>
    /// <param name="mongoClient">The MongoDB client.</param>
    /// <param name="options">The persistence options used to select the database.</param>
    /// <param name="logger">The logger used for unresolved message types.</param>
    /// <param name="typeRegistry">The registry used to resolve stored message types.</param>
    public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry)
        : this(mongoClient, options, "Aggregator", logger, typeRegistry)
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
    public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, string collectionName, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));

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
        try
        {
            await EnsureIndexesAsync().ConfigureAwait(false);

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
                Version = 1
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
        return snapshot.ResolvedMessages.ToList();
    }

    /// <inheritdoc />
    public async Task<IAggregatorSnapshot> GetSnapshotAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureIndexesAsync().ConfigureAwait(false);
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            var docs = await _collection.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);

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

                messages.Add(BsonSerializer.Deserialize(doc.DataBson, type));
                ids.Add(doc.Id);
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
            await EnsureIndexesAsync().ConfigureAwait(false);
            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.Eq("DataBson.CorrelationId", new BsonBinaryData(correlationId, GuidRepresentation.Standard))
            );
            // Name + CorrelationId is effectively unique; DeleteOneAsync avoids a full
            // collection scan after the first match.
            await _collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
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
            await EnsureIndexesAsync().ConfigureAwait(false);
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
        if (snapshot.ResolvedIds.Count == 0) return;

        try
        {
            await EnsureIndexesAsync().ConfigureAwait(false);
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
            await EnsureIndexesAsync().ConfigureAwait(false);
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
    /// Called lazily on first write; the flag is checked before every write to avoid
    /// a round-trip on every call while still retrying after a failure.
    /// </summary>
    private async Task EnsureIndexesAsync()
    {
        if (_indexesEnsured) return;

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

            await _collection.Indexes.CreateManyAsync([nameIndex, nameCorrelationIndex]).ConfigureAwait(false);
            _indexesEnsured = true;
        }
        catch
        {
            // Leave the flag false so a subsequent call retries.
            throw;
        }
    }

    /// <summary>
    /// Internal document type for aggregator storage (not constrained by IProcessManagerData).
    /// </summary>
    private class AggregatorDocument
    {
        public Guid Id { get; set; }
        public int Version { get; set; }
        public BsonDocument DataBson { get; set; } = default!;
        public string DataTypeName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }
}
