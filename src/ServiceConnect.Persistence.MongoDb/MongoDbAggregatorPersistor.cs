using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;
using ServiceConnect.Services;

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

    public MongoDbAggregatorPersistor(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry)
        : this(mongoClient, options, "Aggregator", logger, typeRegistry)
    {
    }

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
                // bump between store and read doesn't invalidate the lookup (A-20).
                DataTypeName = dataType.FullName!,
                Version = 1
            }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to insert aggregator data for '{name}'.", ex);
        }
    }

    public async Task<IList<object>> GetDataAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            var docs = await _collection.Find(filter).ToListAsync(cancellationToken).ConfigureAwait(false);
            // Pre-size the result list to doc count so it doesn't resize as we append (P-59).
            var result = new List<object>(docs.Count);

            foreach (var doc in docs)
            {
                if (!_typeRegistry.TryResolve(doc.DataTypeName, out var type))
                {
                    _logger.LogWarning("Cannot resolve type '{TypeName}' for aggregator data", doc.DataTypeName);
                    continue;
                }

                result.Add(BsonSerializer.Deserialize(doc.DataBson, type));
            }

            return result;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to get aggregator data for '{name}'.", ex);
        }
    }

    public async Task RemoveDataAsync(string name, Guid correlationId, CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.Eq("DataBson.CorrelationId", new BsonBinaryData(correlationId, GuidRepresentation.Standard))
            );
            // Name + CorrelationId is effectively unique; DeleteOneAsync avoids a full
            // collection scan after the first match (P-057).
            await _collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove aggregator data for '{name}' with correlationId '{correlationId}'.", ex);
        }
    }

    public async Task RemoveAllAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            await _collection.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove all aggregator data for '{name}'.", ex);
        }
    }

    public async Task<int> CountAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
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
    /// a round-trip on every call while still retrying after a failure (P-053).
    /// </summary>
    private async Task EnsureIndexesAsync()
    {
        if (_indexesEnsured) return;

        try
        {
            // Single-field index on Name supports GetDataAsync, RemoveAllAsync, CountAsync
            var nameIndex = new CreateIndexModel<AggregatorDocument>(
                Builders<AggregatorDocument>.IndexKeys.Ascending(x => x.Name));

            // Compound index on (Name, DataBson.CorrelationId) supports RemoveDataAsync (P-053)
            var nameCorrelationIndex = new CreateIndexModel<AggregatorDocument>(
                Builders<AggregatorDocument>.IndexKeys
                    .Ascending(x => x.Name)
                    .Ascending("DataBson.CorrelationId"));

            await _collection.Indexes.CreateManyAsync([nameIndex, nameCorrelationIndex]).ConfigureAwait(false);
            _indexesEnsured = true;
        }
        catch
        {
            // Leave the flag false so a subsequent call retries (C-06).
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
