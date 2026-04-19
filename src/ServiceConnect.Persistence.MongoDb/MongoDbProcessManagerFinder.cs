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
public sealed class MongoDbProcessManagerFinder : IProcessManagerFinder
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly ILogger<MongoDbProcessManagerFinder> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _indexedCollections = new();

    // Cached compiled delegates for InsertDataTypedAsync<T>, keyed by concrete data type.
    // Avoid MakeGenericMethod + MethodInfo.Invoke on every insert call.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<MongoDbProcessManagerFinder, IProcessManagerData, string, CancellationToken, Task>>
        InsertDelegateCache = new();
    public MongoDbProcessManagerFinder(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbProcessManagerFinder> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        _logger = logger;

        try
        {
            _mongoDatabase = mongoClient.GetDatabase(options.DatabaseName);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for process manager persistence.", ex);
        }
    }

    public async Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType())
                      ?? mapper.Mappings.FirstOrDefault(m => m.MessageType == typeof(Message));

        if (mapping == null)
            throw new InvalidOperationException(
                $"No property mapping configured for message type '{message.GetType().FullName}' or the base Message type.");

        var collectionName = typeof(T).Name;
        var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
        await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

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
            return await collection.Find(lambda).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collectionName = GetCollectionName(data);
        var dataType = data.GetType();

        // Look up or build the compiled delegate for this concrete type. MakeGenericMethod
        // is called only once per type; subsequent calls use the cached delegate directly,
        // avoiding reflection overhead on the hot path.
        //
        // InsertDataTypedAsync<T> takes a T parameter, so we build a thin Expression wrapper
        // that accepts IProcessManagerData and down-casts to T before the real call — matching
        // the pattern used by InMemoryProcessManagerFinder.BuildMemoryDataFactory.
        var insertDelegate = InsertDelegateCache.GetOrAdd(dataType, static t =>
        {
            var genericMethod = typeof(MongoDbProcessManagerFinder)
                .GetMethod(nameof(InsertDataTypedAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(t);

            var finderParam    = Expression.Parameter(typeof(MongoDbProcessManagerFinder), "finder");
            var dataParam      = Expression.Parameter(typeof(IProcessManagerData), "data");
            var collectionParam = Expression.Parameter(typeof(string), "collectionName");
            var ctParam        = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

            // Cast IProcessManagerData → T so the call matches the typed parameter.
            var castedData = Expression.Convert(dataParam, t);
            var call = Expression.Call(finderParam, genericMethod, castedData, collectionParam, ctParam);

            return Expression.Lambda<Func<MongoDbProcessManagerFinder, IProcessManagerData, string, CancellationToken, Task>>(
                call, finderParam, dataParam, collectionParam, ctParam).Compile();
        });

        try
        {
            await insertDelegate(this, data, collectionName, cancellationToken).ConfigureAwait(false);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to insert process manager data with CorrelationId '{data.CorrelationId}'.", ex);
        }
    }

    private async Task InsertDataTypedAsync<T>(T data, string collectionName, CancellationToken cancellationToken) where T : class, IProcessManagerData
    {
        var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
        await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

        var mongoDbData = new MongoDbData<T>
        {
            Data = data,
            Version = 1,
            Id = Guid.NewGuid()
        };

        await collection.InsertOneAsync(mongoDbData, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDataAsync<T>(IPersistenceData<T> persistenceData, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collectionName = GetCollectionName(persistenceData.Data);
        var versionData = (MongoDbData<T>)persistenceData;
        int currentVersion = versionData.Version;

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

            var filter = Builders<MongoDbData<T>>.Filter.And(
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, versionData.Data.CorrelationId),
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Version, currentVersion)
            );
            versionData.Version = currentVersion + 1;
            var result = await collection.ReplaceOneAsync(filter, versionData, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsAcknowledged && result.ModifiedCount == 0)
            {
                // Revert the version so the in-memory object stays consistent on failure
                versionData.Version = currentVersion;
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {versionData.Data.CorrelationId} and Version {currentVersion} could not be updated.");
            }
        }
        catch (ConcurrencyException)
        {
            throw;
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (MongoException ex)
        {
            // Revert the version so the in-memory object stays consistent on transport failure
            versionData.Version = currentVersion;
            throw new PersistenceException(
                $"Failed to update process manager data with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
    }

    public async Task DeleteDataAsync<T>(IPersistenceData<T> persistenceData, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collectionName = GetCollectionName(persistenceData.Data);

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

            // CorrelationId is unique per process manager type; DeleteOneAsync is sufficient.
            var filter = Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, persistenceData.Data.CorrelationId);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to delete process manager data with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
    }

    private async Task EnsureCorrelationIdIndexAsync<T>(IMongoCollection<MongoDbData<T>> collection) where T : class, IProcessManagerData
    {
        var collectionName = typeof(T).Name;
        if (!_indexedCollections.TryAdd(collectionName, true)) return;

        var indexKeys = Builders<MongoDbData<T>>.IndexKeys.Ascending(x => x.Data.CorrelationId);
        var indexModel = new CreateIndexModel<MongoDbData<T>>(indexKeys, new CreateIndexOptions { Unique = true });
        try
        {
            await collection.Indexes.CreateOneAsync(indexModel).ConfigureAwait(false);
        }
        catch
        {
            // Roll back the marker so a subsequent call retries index creation.
            _indexedCollections.TryRemove(collectionName, out _);
            throw;
        }
    }

    private static string GetCollectionName(IProcessManagerData data)
    {
        return data.GetType().Name;
    }
}
