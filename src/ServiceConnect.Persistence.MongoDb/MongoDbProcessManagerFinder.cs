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

    // Cached compiled delegates for InsertDataTypedAsync<T>, keyed by concrete data type.
    // Avoids MakeGenericMethod + MethodInfo.Invoke on every insert call (R-007, P-006).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<MongoDbProcessManagerFinder, IProcessManagerData, string, CancellationToken, Task>>
        InsertDelegateCache = new();
    private const string TimeoutsCollectionName = "Timeouts";
    /// <summary>
    /// Interval after which the polling service is asked to re-query when no future timeouts
    /// are scheduled. Balances polling chatter against responsiveness to freshly-inserted
    /// timeouts discovered after a query.
    /// </summary>
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);

    public MongoDbProcessManagerFinder(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbProcessManagerFinder> logger)
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
        // avoiding reflection overhead on the hot path (R-007, P-006).
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

        var filter = Builders<MongoDbData<T>>.Filter
            .Eq(x => x.Data.CorrelationId, mongoDbData.Data.CorrelationId);
        await collection.ReplaceOneAsync(filter, mongoDbData, new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateDataAsync<T>(IPersistenceData<T> persistenceData, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collectionName = GetCollectionName(persistenceData.Data);

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

            var versionData = (MongoDbData<T>)persistenceData;
            int currentVersion = versionData.Version;

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

    public async Task DeleteDataAsync<T>(IPersistenceData<T> persistenceData, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collectionName = GetCollectionName(persistenceData.Data);

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            await EnsureCorrelationIdIndexAsync(collection).ConfigureAwait(false);

            // CorrelationId is unique per process manager type; DeleteOneAsync is sufficient (P-057).
            var filter = Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, persistenceData.Data.CorrelationId);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to delete process manager data with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
    }

    public async Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            await EnsureTimeoutIndexAsync(collection).ConfigureAwait(false);

            await collection.InsertOneAsync(timeoutData, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to insert timeout data.", ex);
        }
    }

    public async Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var retval = new TimeoutsBatch { DueTimeouts = [] };
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);
            var utcNow = DateTime.UtcNow;

            // Lock and retrieve all due timeouts in two round-trips rather than N (P-14).
            // Each poll uses a unique session id so a losing concurrent consumer cannot
            // see rows another consumer has just locked; without this, the follow-up
            // Find(Locked == true) would return the union of every concurrent poll's
            // locked rows, causing double-dispatch (H-1).
            var sessionId = Guid.NewGuid();
            var dueUnlockedFilter = Builders<TimeoutData>.Filter.Eq(x => x.Locked, false) &
                                    Builders<TimeoutData>.Filter.Lte(x => x.Time, utcNow);
            var lockUpdate = Builders<TimeoutData>.Update
                .Set(x => x.Locked, true)
                .Set(x => x.LockedBy, sessionId);
            var updateResult = await collection.UpdateManyAsync(dueUnlockedFilter, lockUpdate, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (updateResult.IsAcknowledged && updateResult.ModifiedCount > 0)
            {
                var ownedFilter = Builders<TimeoutData>.Filter.Eq(x => x.LockedBy, sessionId) &
                                  Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
                var dueLocked = await collection.Find(ownedFilter).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var doc in dueLocked)
                    retval.DueTimeouts.Add(doc);
            }

            // Determine next query time from the earliest future unlocked timeout.
            // Project to Time only — avoids fetching the Headers dictionary and other
            // large fields we don't need for scheduling purposes (P-055).
            var nextQueryTime = DateTime.MaxValue;
            var futureFilter = Builders<TimeoutData>.Filter.Gt(x => x.Time, utcNow) &
                               Builders<TimeoutData>.Filter.Eq(x => x.Locked, false);
            var futureSort = Builders<TimeoutData>.Sort.Ascending(x => x.Time);
            var nextTimeProjection = Builders<TimeoutData>.Projection
                .Include(x => x.Id)
                .Include(x => x.Time);
            var nextTimeout = await collection.Find(futureFilter)
                .Sort(futureSort)
                .Project<TimeoutData>(nextTimeProjection)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            if (nextTimeout is not null)
            {
                nextQueryTime = nextTimeout.Time;
            }

            if (nextQueryTime == DateTime.MaxValue)
            {
                nextQueryTime = utcNow.Add(DefaultNextQueryInterval);
            }

            retval.NextQueryTime = nextQueryTime;
            return retval;
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to get timeouts batch.", ex);
        }
    }

    public async Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var collection = _mongoDatabase.GetCollection<TimeoutData>(TimeoutsCollectionName);

            var filter = Builders<TimeoutData>.Filter.Eq(x => x.Id, id) &
                         Builders<TimeoutData>.Filter.Eq(x => x.Locked, true);
            await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException($"Failed to remove dispatched timeout with Id '{id}'.", ex);
        }
    }

    private async Task EnsureCorrelationIdIndexAsync<T>(IMongoCollection<MongoDbData<T>> collection) where T : class, IProcessManagerData
    {
        var collectionName = typeof(T).Name;
        if (!_indexedCollections.TryAdd(collectionName, true)) return;

        var indexKeys = Builders<MongoDbData<T>>.IndexKeys.Ascending(x => x.Data.CorrelationId);
        var indexModel = new CreateIndexModel<MongoDbData<T>>(indexKeys);
        try
        {
            await collection.Indexes.CreateOneAsync(indexModel).ConfigureAwait(false);
        }
        catch
        {
            // Roll back the marker so a subsequent call retries index creation (C-06).
            _indexedCollections.TryRemove(collectionName, out _);
            throw;
        }
    }

    private async Task EnsureTimeoutIndexAsync(IMongoCollection<TimeoutData> collection)
    {
        if (_timeoutIndexEnsured) return;

        try
        {
            // Primary Id index (used for exact-key deletes)
            var idIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys.Ascending(x => x.Id));

            // Compound index covering the due-timeout query: Locked + Time (P-022)
            var lockedTimeIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.Locked)
                    .Ascending(x => x.Time));

            // Compound index covering the ownership query: LockedBy + Locked (P-022)
            var lockedByIndexModel = new CreateIndexModel<TimeoutData>(
                Builders<TimeoutData>.IndexKeys
                    .Ascending(x => x.LockedBy)
                    .Ascending(x => x.Locked));

            await collection.Indexes.CreateManyAsync([idIndexModel, lockedTimeIndexModel, lockedByIndexModel]).ConfigureAwait(false);
            _timeoutIndexEnsured = true;
        }
        catch
        {
            // Leave the flag false so a subsequent call retries (C-06).
            throw;
        }
    }

    private static string GetCollectionName(IProcessManagerData data)
    {
        return data.GetType().Name;
    }
}
