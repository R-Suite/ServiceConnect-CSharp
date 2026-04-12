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

    public MongoDbAggregatorPersistor(MongoDbPersistenceOptions options, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry)
        : this(options, "Aggregator", logger, typeRegistry)
    {
    }

    public MongoDbAggregatorPersistor(MongoDbPersistenceOptions options, string collectionName, ILogger<MongoDbAggregatorPersistor> logger, IMessageTypeRegistry typeRegistry)
    {
        _logger = logger;
        _typeRegistry = typeRegistry ?? throw new ArgumentNullException(nameof(typeRegistry));

        try
        {
            var client = MongoClientFactory.Create(options);
            var database = client.GetDatabase(options.DatabaseName);
            _collection = database.GetCollection<AggregatorDocument>(collectionName);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for aggregator persistence.", ex);
        }
    }

    public void InsertData(object data, string name)
    {
        try
        {
            var dataType = data.GetType();
            var dataBson = data.ToBsonDocument(dataType);

            _collection.InsertOne(new AggregatorDocument
            {
                Name = name,
                DataBson = dataBson,
                DataTypeName = dataType.AssemblyQualifiedName!,
                Version = 1
            });
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to insert aggregator data for '{name}'.", ex);
        }
    }

    public IList<object> GetData(string name)
    {
        try
        {
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            var docs = _collection.Find(filter).ToList();
            var result = new List<object>();

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

    public void RemoveData(string name, Guid correlationId)
    {
        try
        {
            var filter = Builders<AggregatorDocument>.Filter.And(
                Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name),
                Builders<AggregatorDocument>.Filter.Eq("DataBson.CorrelationId", new BsonBinaryData(correlationId, GuidRepresentation.Standard))
            );
            _collection.DeleteMany(filter);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove aggregator data for '{name}' with correlationId '{correlationId}'.", ex);
        }
    }

    public int Count(string name)
    {
        try
        {
            var filter = Builders<AggregatorDocument>.Filter.Eq(x => x.Name, name);
            var count = _collection.CountDocuments(filter);
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to count aggregator data for '{name}'.", ex);
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
