using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// MongoDB implementation of IProcessManagerFinder.
/// Supports both standard and SSL connections via MongoDbPersistenceOptions.
/// Uses locking mechanism for timeout batch retrieval to prevent duplicate dispatch.
/// </summary>
public sealed class MongoDbProcessManagerFinder : IProcessManagerFinder, ITimeoutStore
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly ILogger<MongoDbProcessManagerFinder> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _indexedCollections = new();
    private volatile bool _timeoutIndexEnsured;
    private const string TimeoutsCollectionName = "Timeouts";

    public event TimeoutInsertedDelegate? TimeoutInserted;

    public MongoDbProcessManagerFinder(MongoDbPersistenceOptions options, ILogger<MongoDbProcessManagerFinder> logger)
    {
        _logger = logger;

        try
        {
            var client = MongoClientFactory.Create(options);
            _mongoDatabase = client.GetDatabase(options.DatabaseName);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for process manager persistence.", ex);
        }
    }

    public IPersistenceData<T>? FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
    {
        var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType())
                      ?? mapper.Mappings.FirstOrDefault(m => m.MessageType == typeof(Message));

        if (mapping == null)
            throw new InvalidOperationException(
                $"No property mapping configured for message type '{message.GetType().FullName}' or the base Message type.");

        var collectionName = typeof(T).Name;
        var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
        EnsureCorrelationIdIndex(collection);

        object? msgPropValue;

        try
        {
            msgPropValue = mapping.MessageProp.Invoke(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evaluate message property mapping for {MessageType}.", message.GetType().Name);
            throw new PersistenceException(
                $"Failed to evaluate message property mapping for message type '{message.GetType().Name}'.", ex);
        }

        if (msgPropValue is null)
        {
            throw new ArgumentException("Message property expression evaluates to null.");
        }

        try
        {
            // Build dynamic expression to query the mapped property hierarchy
            ParameterExpression pe = Expression.Parameter(typeof(MongoDbData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MongoDbData<T>).GetTypeInfo().GetProperty("Data")!);
            foreach (var prop in mapping.PropertiesHierarchy.Reverse())
            {
                left = Expression.Property(left, left.Type, prop.Key);
            }

            Expression right = Expression.Constant(msgPropValue, msgPropValue.GetType());
            Expression expression;

            try
            {
                expression = Expression.Equal(left, right);
            }
            catch (InvalidOperationException ex)
            {
                throw new PersistenceException("Mapped incompatible types of ProcessManager Data and Message properties.", ex);
            }

            var lambda = Expression.Lambda<Func<MongoDbData<T>, bool>>(expression, pe);
            return collection.Find(lambda).FirstOrDefault()!;
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to find process manager data for message type '{message.GetType().Name}'.", ex);
        }
    }

    public void InsertData(IProcessManagerData data)
    {
        var collectionName = GetCollectionName(data);
        var dataType = data.GetType();

        try
        {
            // Use reflection to call the generic InsertDataTyped<T> method with the actual
            // data type rather than the interface, so MongoDB serializes/deserializes with
            // a consistent generic type parameter across Insert and Find operations.
            var method = GetType().GetMethod(nameof(InsertDataTyped),
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var genericMethod = method.MakeGenericMethod(dataType);
            genericMethod.Invoke(this, [data, collectionName]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is MongoException mongoEx)
        {
            throw new PersistenceException(
                $"Failed to insert process manager data with CorrelationId '{data.CorrelationId}'.", mongoEx);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to insert process manager data with CorrelationId '{data.CorrelationId}'.", ex);
        }
    }

    private void InsertDataTyped<T>(T data, string collectionName) where T : class, IProcessManagerData
    {
        var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
        EnsureCorrelationIdIndex(collection);

        var mongoDbData = new MongoDbData<T>
        {
            Data = data,
            Version = 1,
            Id = Guid.NewGuid()
        };

        var filter = Builders<MongoDbData<T>>.Filter
            .Eq(x => x.Data.CorrelationId, mongoDbData.Data.CorrelationId);
        collection.ReplaceOne(filter, mongoDbData, new ReplaceOptions { IsUpsert = true });
    }

    public void UpdateData<T>(IPersistenceData<T> persistenceData) where T : class, IProcessManagerData
    {
        var collectionName = GetCollectionName(persistenceData.Data);

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            EnsureCorrelationIdIndex(collection);

            var versionData = (MongoDbData<T>)persistenceData;
            int currentVersion = versionData.Version;

            var filter = Builders<MongoDbData<T>>.Filter.And(
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, versionData.Data.CorrelationId),
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Version, currentVersion)
            );
            versionData.Version = currentVersion + 1;
            var result = collection.ReplaceOne(filter, versionData);

            if (result.IsAcknowledged && result.ModifiedCount == 0)
            {
                // Revert the version so the in-memory object stays consistent on failure
                versionData.Version = currentVersion;
                throw new PersistenceException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {versionData.Data.CorrelationId} and Version {currentVersion} could not be updated.");
            }
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (MongoException ex)
        {
            // Revert the version so the in-memory object stays consistent on transport failure
            if (persistenceData is MongoDbData<T> vd)
                vd.Version = vd.Version > 0 ? vd.Version - 1 : 0;
            throw new PersistenceException(
                $"Failed to update process manager data with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
    }

    public void DeleteData<T>(IPersistenceData<T> persistenceData) where T : class, IProcessManagerData
    {
        var collectionName = GetCollectionName(persistenceData.Data);

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            EnsureCorrelationIdIndex(collection);

            var filter = Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, persistenceData.Data.CorrelationId);
            collection.DeleteMany(filter);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to delete process manager data with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
    }

    public void InsertTimeout(TimeoutData timeoutData)
    {
        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            EnsureTimeoutIndex(collection);

            collection.InsertOne(timeoutData);
            TimeoutInserted?.Invoke(timeoutData.Time);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to insert timeout data.", ex);
        }
    }

    public TimeoutsBatch GetTimeoutsBatch()
    {
        try
        {
            var retval = new TimeoutsBatch { DueTimeouts = [] };
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var utcNow = DateTime.UtcNow;

            // Find all due timeouts and lock each one to prevent duplicate dispatch
            bool doQuery = true;
            while (doQuery)
            {
                var filter = Builders<TimeoutData>.Filter.Eq(x => x.Locked, false) &
                             Builders<TimeoutData>.Filter.Lte(x => x.Time, utcNow);

                var update = Builders<TimeoutData>.Update.Set(x => x.Locked, true);
                var result = collection.FindOneAndUpdate(
                    filter,
                    update,
                    new FindOneAndUpdateOptions<TimeoutData> { ReturnDocument = ReturnDocument.After });

                if (result is null)
                {
                    doQuery = false;
                }
                else
                {
                    retval.DueTimeouts.Add(result);
                }
            }

            // Determine next query time from the earliest future unlocked timeout
            var nextQueryTime = DateTime.MaxValue;
            var futureFilter = Builders<TimeoutData>.Filter.Gt(x => x.Time, utcNow) &
                               Builders<TimeoutData>.Filter.Eq(x => x.Locked, false);
            var futureSort = Builders<TimeoutData>.Sort.Ascending(x => x.Time);
            var nextTimeout = collection.Find(futureFilter).Sort(futureSort).FirstOrDefault();

            if (nextTimeout is not null)
            {
                nextQueryTime = nextTimeout.Time;
            }

            if (nextQueryTime == DateTime.MaxValue)
            {
                nextQueryTime = utcNow.AddMinutes(1);
            }

            retval.NextQueryTime = nextQueryTime;
            return retval;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to get timeouts batch.", ex);
        }
    }

    public void RemoveDispatchedTimeout(Guid id)
    {
        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            collection.DeleteOne(filter);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}'.", ex);
        }
    }

    private void EnsureCorrelationIdIndex<T>(IMongoCollection<MongoDbData<T>> collection) where T : class, IProcessManagerData
    {
        var collectionName = typeof(T).Name;
        if (!_indexedCollections.TryAdd(collectionName, true)) return;

        var indexKeys = Builders<MongoDbData<T>>.IndexKeys.Ascending(x => x.Data.CorrelationId);
        var indexModel = new CreateIndexModel<MongoDbData<T>>(indexKeys);
        collection.Indexes.CreateOne(indexModel);
    }

    private void EnsureTimeoutIndex(IMongoCollection<TimeoutData> collection)
    {
        if (_timeoutIndexEnsured) return;
        _timeoutIndexEnsured = true;

        var indexKeys = Builders<TimeoutData>.IndexKeys.Ascending(x => x.Id);
        var indexModel = new CreateIndexModel<TimeoutData>(indexKeys);
        collection.Indexes.CreateOne(indexModel);
    }

    private static string GetCollectionName(IProcessManagerData data)
    {
        return data.GetType().Name;
    }
}
