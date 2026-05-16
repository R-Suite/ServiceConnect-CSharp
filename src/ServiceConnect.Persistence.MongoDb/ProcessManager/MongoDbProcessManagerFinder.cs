using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// MongoDB implementation of IProcessManagerFinder.
/// Supports both standard and SSL connections via MongoDbPersistenceOptions.
/// Uses locking mechanism for timeout batch retrieval to prevent duplicate dispatch.
/// </summary>
internal sealed partial class MongoDbProcessManagerFinder : IProcessManagerFinder
{
    private readonly IMongoDatabase _mongoDatabase;
    private readonly ILogger<MongoDbProcessManagerFinder> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _indexedCollections = new(StringComparer.Ordinal);
    // _indexCreationSemaphore is intentionally NOT Disposed:
    // SemaphoreSlim.Dispose only releases the lazily-allocated WaitHandle, and we never call
    // AvailableWaitHandle, so disposal is a functional no-op. A concurrent caller's Release()
    // on a disposed semaphore would throw ObjectDisposedException out of the unwind path,
    // which we cannot prevent without holding GC references to every caller. Mirrors the
    // Connection / ProducerConnection / Producer / Bus pattern (Phases 4 + 6 + 7).
    private readonly SemaphoreSlim _indexCreationSemaphore = new(1, 1);
    private static readonly HashSet<int> BenignIndexCodes = [85, 86]; // IndexOptionsConflict, IndexKeySpecsConflict

    // Cached compiled delegates for InsertDataTypedAsync<T>, keyed by concrete data type.
    // Avoid MakeGenericMethod + MethodInfo.Invoke on every insert call.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<MongoDbProcessManagerFinder, IProcessManagerData, string, CancellationToken, Task>>
        InsertDelegateCache = new();

    // Cached compiled delegates for the EnsureCorrelationIdIndexAsync<T> startup-time
    // dispatch path. Built once per saga data type and reused for every subsequent
    // hosted-service invocation.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<MongoDbProcessManagerFinder, CancellationToken, Task>>
        EnsureIndexDelegateCache = new();

    static MongoDbProcessManagerFinder()
    {
        // Ensure the canonical Guid serializer is registered before any direct-ctor
        // path serialises a Guid. DI factories also call this; the static ctor covers
        // tests and custom compositions that bypass DI.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();
    }

    /// <summary>
    /// Creates a process-manager finder backed by MongoDB.
    /// </summary>
    /// <param name="mongoClient">The MongoDB client.</param>
    /// <param name="options">The persistence options used to select the database.</param>
    /// <param name="logger">The logger used for mapping failures.</param>
    /// <param name="timeProvider">Reserved for future time-dependent behavior.</param>
    public MongoDbProcessManagerFinder(IMongoClient mongoClient, MongoDbPersistenceOptions options, ILogger<MongoDbProcessManagerFinder> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mongoClient);
        ArgumentNullException.ThrowIfNull(logger);
        // Reserved for future time-dependent behavior (e.g. lease-based document locks).
        _ = timeProvider;
        _logger = logger;

        try
        {
            _mongoDatabase = mongoClient.GetDatabase(options.DatabaseName);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException("Failed to connect to MongoDB for process manager persistence.", ex);
        }

        // Saga state is correctness-sensitive: w:0 silently loses concurrent updates and
        // wedges the saga on the next real conflict because the version field advances
        // without the matching ReplaceOne hitting a row. Reject loudly at startup.
        if (!mongoClient.Settings.WriteConcern.IsAcknowledged)
        {
            throw new InvalidOperationException(
                "MongoDbProcessManagerFinder requires an acknowledged WriteConcern (w:1 or higher). " +
                "WriteConcern.Unacknowledged (w:0) silently loses concurrent saga updates and " +
                "wedges sagas on the next real conflict because the version field advances. " +
                "Configure mongoClient.Settings.WriteConcern to a value where IsAcknowledged is true.");
        }
    }

    /// <inheritdoc />
    public async Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mapping = (mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType())
                      ?? mapper.Mappings.FirstOrDefault(m => m.MessageType == typeof(Message))) ?? throw new InvalidOperationException(
                $"No property mapping configured for message type '{message.GetType().FullName}' or the base Message type.");
        var collectionName = GetCollectionName<T>();
        var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
        await EnsureCorrelationIdIndexAsync(collection, collectionName, cancellationToken).ConfigureAwait(false);

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
            throw new ArgumentException("Message property expression evaluates to null.", nameof(message));
        }

        try
        {
            // Build dynamic expression to query the mapped property hierarchy
            ParameterExpression pe = Expression.Parameter(typeof(MongoDbData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MongoDbData<T>).GetTypeInfo().GetProperty("Data")!);
            foreach (var prop in mapping.PropertiesHierarchy.Reverse())
            {
                // Resolve the property by walking the type AND its implemented interfaces,
                // so explicit-interface impls (where the property isn't reachable by string
                // name on the runtime type) are matched via their declaring-type PropertyInfo.
                var propInfo = left.Type.GetProperty(prop.Key,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                    ?? left.Type.GetInterfaces()
                        // Property names in PropertiesHierarchy are expected to be unambiguous across
                        // a saga type's implemented interfaces. If two interfaces declare the same
                        // property name, FirstOrDefault here picks whichever the runtime returns first.
                        .Select(i => i.GetProperty(prop.Key, BindingFlags.Public | BindingFlags.Instance))
                        .FirstOrDefault(p => p is not null)
                    ?? throw new InvalidOperationException(
                        $"Property '{prop.Key}' not found on type '{left.Type.FullName}' or its interfaces.");
                left = Expression.MakeMemberAccess(left, propInfo);
            }

            // Coerce the runtime value's type to the declared property type. msgPropValue's
            // runtime type can differ from the saga property's declared type (e.g., the
            // message has int but the saga has long, the message has T but the saga has
            // Nullable<T>, or the message has a concrete type but the saga has an interface).
            // Without the convert, Expression.Equal rejects mismatched primitive types
            // outright (InvalidOperationException), and even when types are compatible the
            // BSON filter renderer uses the runtime type — the BSON path projection silently
            // misses against documents stored under the declared type. Mirror the InMemory
            // finder's GetPredicate(), which has used Convert(valueParam, key.PropertyType)
            // since inception.
            Expression right = Expression.Convert(
                Expression.Constant(msgPropValue, msgPropValue.GetType()),
                left.Type);
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
        catch (BsonException ex)
        {
            // Schema drift: a stored saga document cannot be materialised into the v7 CLR type
            // (e.g. a property's stored BSON type is incompatible with the declared property
            // type, or a missing required field). Wrap as PersistenceException so the caller's
            // catch surface is consistent; the dispatcher will surface this as a permanent
            // dispatch failure rather than NACK-looping the broker.
            throw new PersistenceException(
                $"Schema drift: failed to deserialise saga document for message type '{message.GetType().Name}'. A stored document is incompatible with the current CLR shape.", ex);
        }
        catch (FormatException ex)
        {
            // BsonClassMapSerializer throws bare FormatException when a property's stored
            // BSON type cannot be coerced to the CLR property type (e.g. string-in-BSON when
            // the CLR property is int). Same poison-row mitigation as the BsonException catch.
            throw new PersistenceException(
                $"Schema drift: failed to deserialise saga document for message type '{message.GetType().Name}'. A stored property's BSON type is incompatible with the current CLR shape.", ex);
        }
    }

    /// <inheritdoc />
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

            var finderParam = Expression.Parameter(typeof(MongoDbProcessManagerFinder), "finder");
            var dataParam = Expression.Parameter(typeof(IProcessManagerData), "data");
            var collectionParam = Expression.Parameter(typeof(string), "collectionName");
            var ctParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

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
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Concurrent first-message delivery for the same CorrelationId. The unique index
            // on CorrelationId signals the loser; surface as ConcurrencyException so the
            // caller can re-find the just-committed row and take the update path. The base
            // MongoException catch would otherwise collapse this into a permanent
            // PersistenceException that ProcessManagerProcessor's retry loop can't recover
            // from (it only retries on ConcurrencyException).
            throw new ConcurrencyException(
                $"Concurrent insert detected for CorrelationId '{data.CorrelationId}'; another writer committed first.", ex);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to insert process manager data with CorrelationId '{data.CorrelationId}'.", ex);
        }
        catch (BsonException ex)
        {
            // BSON serialisation failure (e.g. a CLR property cannot be represented in BSON).
            // Surface as PersistenceException so the caller's catch surface is consistent.
            throw new PersistenceException(
                $"BSON serialisation failure inserting saga with CorrelationId '{data.CorrelationId}'.", ex);
        }
    }

    private async Task InsertDataTypedAsync<T>(T data, string collectionName, CancellationToken cancellationToken) where T : class, IProcessManagerData
    {
        var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
        await EnsureCorrelationIdIndexAsync(collection, collectionName, cancellationToken).ConfigureAwait(false);

        var mongoDbData = new MongoDbData<T>
        {
            Data = data,
            Version = 1,
            Id = Guid.NewGuid()
        };

        await collection.InsertOneAsync(mongoDbData, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pre-creates the unique CorrelationId index for the supplied saga data type,
    /// dispatching by reflection to the generic <see cref="EnsureCorrelationIdIndexAsync{T}"/>.
    /// Used by the startup-time hosted service to close the cross-process race window
    /// where two cold-started processes could insert duplicate saga rows before either
    /// one called the lazy index-creation path on the I/O hot path.
    /// </summary>
    /// <remarks>
    /// The compiled delegate is cached per type so the reflection / expression-tree
    /// cost is paid once per saga data type for the lifetime of the process.
    /// </remarks>
    internal Task EnsureCorrelationIdIndexForTypeAsync(Type dataType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataType);

        var del = EnsureIndexDelegateCache.GetOrAdd(dataType, static t =>
        {
            // Build a delegate equivalent to:
            //   (finder, ct) =>
            //   {
            //       var collection = finder._mongoDatabase.GetCollection<MongoDbData<T>>(collectionName, null);
            //       return finder.EnsureCorrelationIdIndexAsync<T>(collection, collectionName, ct);
            //   }
            // Collection name is computed at delegate-build time (deterministic per
            // type T) using the same SanitizeCollectionName(FullName ?? Name) logic as GetCollectionName<T>().
            var dataMongoType = typeof(MongoDbData<>).MakeGenericType(t);

            var collectionMethod = typeof(IMongoDatabase)
                .GetMethods()
                .First(m => string.Equals(m.Name, nameof(IMongoDatabase.GetCollection), StringComparison.Ordinal)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 2)
                .MakeGenericMethod(dataMongoType);

            var ensureMethod = typeof(MongoDbProcessManagerFinder)
                .GetMethod(nameof(EnsureCorrelationIdIndexAsync), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(t);

            var finderParam = Expression.Parameter(typeof(MongoDbProcessManagerFinder), "finder");
            var ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

            var collectionName = SanitizeCollectionName(t.FullName ?? t.Name);

            var dbField = Expression.Field(finderParam, nameof(_mongoDatabase));
            var getCollectionCall = Expression.Call(
                dbField,
                collectionMethod,
                Expression.Constant(collectionName),
                Expression.Constant(null, typeof(MongoCollectionSettings)));

            var call = Expression.Call(
                finderParam,
                ensureMethod,
                getCollectionCall,
                Expression.Constant(collectionName),
                ctParam);

            return Expression.Lambda<Func<MongoDbProcessManagerFinder, CancellationToken, Task>>(
                call, finderParam, ctParam).Compile();
        });

        return del(this, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>Retry contract on cancellation.</b> If <see cref="OperationCanceledException"/>
    /// is thrown, the server-side state is undefined: cancellation may have fired before
    /// or after the server committed the update. A caller that retries without re-reading
    /// state may wedge the saga — if the server has actually committed, the caller's
    /// stale <c>Version</c> will mismatch on the retry's concurrency filter and surface
    /// a spurious <see cref="ConcurrencyException"/>. Callers MUST call
    /// <see cref="FindDataAsync"/> first on retry to refresh the version, then rebuild
    /// the write record around the current server state.
    /// </remarks>
    public async Task UpdateDataAsync<T>(IPersistenceData<T> persistenceData, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        var collectionName = GetCollectionName<T>();
        var versionData = (MongoDbData<T>)persistenceData;
        long currentVersion = versionData.Version;

        // Build a separate write record so the caller's versionData is not mutated
        // by the bump until we see a confirmed success. Any failure path (including
        // OperationCanceledException, TaskCanceledException, or an unexpected
        // exception type) therefore leaves the caller's Version intact.
        //
        // RETRY CONTRACT: a cancelled update has TWO possible server-side states —
        //   (a) cancellation fired BEFORE the server committed the ReplaceOne.
        //       The caller's Version still matches the server; a retry succeeds.
        //   (b) cancellation fired AFTER server commit but BEFORE the client
        //       received ack. The server is now at Version N+1; the caller still
        //       believes Version N; a retry's filter (Version == N) MISSES and
        //       throws ConcurrencyException, even though the write succeeded.
        // Callers that catch OperationCanceledException and intend to retry MUST
        // call FindDataAsync first to re-read the current version and rebuild
        // their write record. This contract is documented in the public xmldoc on
        // UpdateDataAsync above.
        var writeRecord = new MongoDbData<T>
        {
            Id = versionData.Id,
            Version = currentVersion + 1,
            Data = versionData.Data,
        };

        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            await EnsureCorrelationIdIndexAsync(collection, collectionName, cancellationToken).ConfigureAwait(false);

            var filter = Builders<MongoDbData<T>>.Filter.And(
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, versionData.Data.CorrelationId),
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Version, currentVersion)
            );
            var result = await collection.ReplaceOneAsync(filter, writeRecord, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result.IsAcknowledged && result.MatchedCount == 0)
            {
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {versionData.Data.CorrelationId} and Version {currentVersion} could not be updated.");
            }

            // Only reflect the bump on the caller's instance after the write is
            // acknowledged and the filter matched a row.
            versionData.Version = currentVersion + 1;
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
            throw new PersistenceException(
                $"Failed to update process manager data with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
        catch (BsonException ex)
        {
            throw new PersistenceException(
                $"BSON serialisation failure updating saga with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
        catch (FormatException ex)
        {
            // BsonClassMapSerializer FormatException — see FindDataAsync catch for rationale.
            throw new PersistenceException(
                $"Schema drift updating saga with CorrelationId '{persistenceData.Data.CorrelationId}'.", ex);
        }
    }

    /// <inheritdoc />
    public async Task DeleteDataAsync<T>(IPersistenceData<T> persistenceData, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(persistenceData);

        var collectionName = GetCollectionName<T>();
        var expectedVersion = ((MongoDbData<T>)persistenceData).Version;
        var correlationId = persistenceData.Data.CorrelationId;

        DeleteResult result;
        try
        {
            var collection = _mongoDatabase.GetCollection<MongoDbData<T>>(collectionName);
            await EnsureCorrelationIdIndexAsync(collection, collectionName, cancellationToken).ConfigureAwait(false);

            // Match on {CorrelationId, Version} so a delete racing an in-flight update
            // cannot silently drop a saga mid-transition. Same contract as UpdateDataAsync.
            var filter = Builders<MongoDbData<T>>.Filter.And(
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Data.CorrelationId, correlationId),
                Builders<MongoDbData<T>>.Filter.Eq(x => x.Version, expectedVersion));
            result = await collection.DeleteOneAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex)
        {
            throw new PersistenceException(
                $"Failed to delete process manager data with CorrelationId '{correlationId}'.", ex);
        }
        catch (BsonException ex)
        {
            throw new PersistenceException(
                $"BSON failure deleting saga with CorrelationId '{correlationId}'.", ex);
        }
        catch (FormatException ex)
        {
            throw new PersistenceException(
                $"Schema drift deleting saga with CorrelationId '{correlationId}'.", ex);
        }

        if (result.IsAcknowledged && result.DeletedCount == 0)
        {
            throw new ConcurrencyException(
                $"Concurrency conflict: ProcessManagerData with CorrelationId {correlationId} and Version {expectedVersion} could not be deleted.");
        }
    }

    private async Task EnsureCorrelationIdIndexAsync<T>(IMongoCollection<MongoDbData<T>> collection, string collectionName, CancellationToken cancellationToken) where T : class, IProcessManagerData
    {
        // Fast path: index already confirmed by this process instance.
        if (_indexedCollections.ContainsKey(collectionName))
        {
            return;
        }

        await _indexCreationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check under the semaphore so a thread that was waiting while another
            // thread created the index does not issue a redundant CreateOneAsync.
            if (_indexedCollections.ContainsKey(collectionName))
            {
                return;
            }

            var indexKeys = Builders<MongoDbData<T>>.IndexKeys.Ascending(x => x.Data.CorrelationId);
            var indexModel = new CreateIndexModel<MongoDbData<T>>(indexKeys, new CreateIndexOptions { Unique = true });
            try
            {
                await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (BenignIndexCodes.Contains(ex.Code))
            {
                // 85 IndexOptionsConflict / 86 IndexKeySpecsConflict — another process
                // created a compatible index concurrently. Treat as success.
                _logger.LogDebug("Concurrent index creation for '{CollectionName}': {Code} {Message}", collectionName, ex.Code, ex.Message);
            }

            // Flip the marker ONLY after index creation succeeds (or benign conflict).
            // Previously the marker was set before CreateOneAsync so a concurrent caller
            // could short-circuit, skip index creation, and then race an insert before
            // the unique index existed — admitting duplicate CorrelationId rows.
            _indexedCollections.TryAdd(collectionName, true);
        }
        finally
        {
            _indexCreationSemaphore.Release();
        }
    }

    // Mongo collection names containing +`[], from generic type names break tooling
    // (mongosh autocomplete, mongo-express, etc.). Replace those characters with '_'
    // so the collection name is portable. Existing v7 deployments with non-generic
    // saga types are unaffected; v8 deployments with generic saga types must rename
    // their existing collection (see release notes).
    // MA0009: regex is a pure character class — O(n), no backtracking, no ReDoS risk.
#pragma warning disable MA0009
    [GeneratedRegex(@"[+`\[\],]", RegexOptions.None)]
    private static partial Regex CollectionNameSanitizerRegex();
#pragma warning restore MA0009

    internal static string SanitizeCollectionName(string raw)
        => CollectionNameSanitizerRegex().Replace(raw, "_");

    // FullName avoids short-name collisions between two saga data types that share a
    // class name across different namespaces. Name is a last-resort fallback for the
    // rare types where FullName is null (e.g., open generics in reflection contexts).
    private static string GetCollectionName<T>() where T : class, IProcessManagerData
        => SanitizeCollectionName(typeof(T).FullName ?? typeof(T).Name);

    private static string GetCollectionName(IProcessManagerData data)
    {
        var t = data.GetType();
        return SanitizeCollectionName(t.FullName ?? t.Name);
    }
}
