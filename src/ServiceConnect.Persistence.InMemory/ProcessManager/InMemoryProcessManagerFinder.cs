using System.Linq.Expressions;
using System.Reflection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// InMemory implementation of IProcessManagerFinder for testing and rapid development.
/// Compiled predicates are cached by mapping shape so correlation lookups avoid both
/// Expression.Compile and reflection on the hot path.
/// </summary>
/// <remarks>
/// <b>Intended for development and tests.</b> Process-manager state is held in-process and is
/// not durable across restarts; in-flight process-manager instances are lost on restart. Use a
/// durable <see cref="ServiceConnect.Interfaces.IProcessManagerFinder"/> implementation (e.g.
/// the MongoDB finder) for production.
/// </remarks>
internal sealed class InMemoryProcessManagerFinder : IProcessManagerFinder
{
    private readonly ProcessManagerPredicateCache _cache;
    private readonly InMemoryPersistenceState _state;

    internal InMemoryProcessManagerFinder(ProcessManagerPredicateCache cache, TimeProvider? timeProvider = null)
        : this(cache, new InMemoryPersistenceState(timeProvider)) { }

    internal InMemoryProcessManagerFinder(ProcessManagerPredicateCache cache, InMemoryPersistenceState state)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    private const long InitialVersion = 1L;

    /// <summary>
    /// Finds persisted process manager data that matches the supplied message mapping.
    /// </summary>
    public Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(message);

        // Single-pass scan: prefer an exact message-type match, fall back to the base
        // Message wildcard in one iteration of the mapping list.
        var exactMessageType = message.GetType();
        ProcessManagerToMessageMap? mapping = null;
        ProcessManagerToMessageMap? fallback = null;
        foreach (var m in mapper.Mappings)
        {
            if (m.MessageType == exactMessageType) { mapping = m; break; }
            if (fallback == null && m.MessageType == typeof(Message))
            {
                fallback = m;
            }
        }
        mapping ??= fallback;

        if (mapping == null)
        {
            throw new InvalidOperationException(
                $"No property mapping configured for message type '{message.GetType().FullName}' or the base Message type.");
        }

        object? msgPropValue;
        try
        {
            msgPropValue = mapping.MessageProp.Invoke(message);
        }
        catch (Exception ex)
        {
            throw new PersistenceException(
                $"Failed to evaluate message property mapping for message type '{message.GetType().Name}'.", ex);
        }

        if (msgPropValue is null)
        {
            throw new ArgumentException("Message property expression evaluates to null", nameof(message));
        }

        var predicate = GetPredicate<T>(mapping.PropertiesHierarchy, msgPropValue.GetType());

        _state.SyncRoot.EnterReadLock();
        try
        {
            return Task.FromResult<IPersistenceData<T>?>(FindMatchingItem<T>(msgPropValue, predicate));
        }
        finally
        {
            _state.SyncRoot.ExitReadLock();
        }
    }

    /// <summary>
    /// Iterates the saga store looking for a row whose <typeparamref name="T"/>-typed
    /// payload satisfies <paramref name="predicate"/>.
    /// </summary>
    /// <remarks>
    /// <b>Multi-saga support:</b> this implementation iterates every entry in the
    /// partitioned saga provider, and the keys are bare correlation-id strings (no
    /// type prefix). Entries whose wrapper type does not match <typeparamref name="T"/>
    /// are skipped — hosting multiple saga types through a single finder instance is
    /// supported, with linear-in-total-rows lookup overhead. For high-row-count
    /// production deployments use the Mongo persistor instead (per-saga-type
    /// collections give O(log n) lookup via the unique CorrelationId index).
    /// </remarks>
    private MemoryData<T>? FindMatchingItem<T>(object msgPropValue, Func<MemoryData<T>, object, bool> predicate)
        where T : class, IProcessManagerData
    {
        foreach (var key in _state.SagaProvider.Keys())
        {
            if (!_state.SagaProvider.TryGet<string, object>(key.ToString()!, out var value) || value is null)
            {
                continue; // removed concurrently by another DeleteDataAsync on the partitioned saga store
            }

            if (value is MemoryData<T> typed)
            {
                if (predicate(typed, msgPropValue))
                {
                    // Carry Id forward so callers see the same stable Guid that was
                    // stamped at insert — matches Mongo's persisted _id contract.
                    return new MemoryData<T> { Id = typed.Id, Data = DeepClone.Clone(typed.Data), Version = typed.Version };
                }
            }
            // Skip entries whose wrapper type doesn't match T. Mongo persists each saga type to
            // its own collection so a FindData<TA> query never returns TB rows; the InMemory
            // store uses a single flat dictionary keyed by correlation-id-as-string, so we
            // simply pass over unrelated saga types. Hosting >1 saga type per worker (a common
            // dev/test topology) used to throw `InvalidOperationException` here — which is
            // not a `ConcurrencyException`, so the dispatcher had no retry path and the
            // worker dispatched permanently-failed messages instead of resolving the saga.
        }
        return null;
    }

    private Func<MemoryData<T>, object, bool> GetPredicate<T>(
        IReadOnlyDictionary<string, Type> propertiesHierarchy, Type propertyType)
        where T : class, IProcessManagerData
    {
        var cacheKey = new ProcessManagerPredicateCache.PredicateCacheKey(typeof(T), propertiesHierarchy, propertyType);
        var compiled = _cache.CompiledPredicates.GetOrAdd(cacheKey, static key =>
        {
            var dataParam = Expression.Parameter(typeof(MemoryData<>).MakeGenericType(key.T), "d");
            var valueParam = Expression.Parameter(typeof(object), "value");

            Expression left = Expression.Property(dataParam, dataParam.Type.GetProperty(nameof(MemoryData<IProcessManagerData>.Data))!);
            foreach (var prop in key.PropertiesHierarchy.Reverse())
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

            Expression right = Expression.Convert(valueParam, key.PropertyType);
            var eq = Expression.Equal(left, right);

            var delegateType = typeof(Func<,,>).MakeGenericType(dataParam.Type, typeof(object), typeof(bool));
            return Expression.Lambda(delegateType, eq, dataParam, valueParam).Compile();
        });
        return (Func<MemoryData<T>, object, bool>)compiled;
    }

    /// <summary>
    /// Inserts a new process manager record into the in-memory store.
    /// </summary>
    public Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);

        var factory = _cache.MemoryDataFactories.GetOrAdd(data.GetType(), BuildMemoryDataFactory);
        // Deep-clone before storing so the caller's subsequent mutations (or a worker
        // retrying with its in-memory snapshot) do not mutate state already persisted.
        var memoryData = factory(DeepClone.Clone(data));

        _state.SyncRoot.EnterWriteLock();
        try
        {
            string key = data.CorrelationId.ToString();
            if (_state.SagaProvider.Contains(key))
            {
                // Concurrent first-message delivery for the same CorrelationId. Surface as
                // ConcurrencyException to match MongoDbProcessManagerFinder so callers (and
                // ProcessManagerProcessor's retry loop, which only retries on
                // ConcurrencyException) see a consistent contract across persistors.
                throw new ConcurrencyException(
                    $"Concurrent insert detected for CorrelationId '{data.CorrelationId}'; another writer committed first.");
            }

            // Saga state has no TTL: lifetime is managed explicitly via Delete.
            // Background expiry must never silently drop a live saga.
            _state.SagaProvider.Add(key, memoryData);
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    // One-time compiled factory per concrete data type, avoiding per-call reflection
    // (GetMethods + MakeGenericMethod + Invoke) on the hot persistence path.
    private static Func<IProcessManagerData, object> BuildMemoryDataFactory(Type dataType)
    {
        var memoryDataType = typeof(MemoryData<>).MakeGenericType(dataType);
        var param = Expression.Parameter(typeof(IProcessManagerData), "d");
        var casted = Expression.Convert(param, dataType);
        var dataProp = memoryDataType.GetProperty(nameof(MemoryData<IProcessManagerData>.Data))!;
        var versionProp = memoryDataType.GetProperty(nameof(MemoryData<IProcessManagerData>.Version))!;
        var idProp = memoryDataType.GetProperty(nameof(MemoryData<IProcessManagerData>.Id))!;
        var guidNewGuid = typeof(Guid).GetMethod(nameof(Guid.NewGuid))!;
        var initExpr = Expression.MemberInit(
            Expression.New(memoryDataType),
            Expression.Bind(dataProp, casted),
            Expression.Bind(versionProp, Expression.Constant(InitialVersion)),
            Expression.Bind(idProp, Expression.Call(guidNewGuid)));
        var lambda = Expression.Lambda<Func<IProcessManagerData, object>>(
            Expression.Convert(initExpr, typeof(object)), param);
        return lambda.Compile();
    }

    /// <summary>
    /// Updates an existing process manager record if its version matches.
    /// </summary>
    public Task UpdateDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            var newData = (MemoryData<T>)data;
            string key = data.Data.CorrelationId.ToString();

            if (!_state.SagaProvider.Contains(key))
            {
                throw new PersistenceException(
                    $"ProcessManagerData with CorrelationId {key} does not exist in memory.");
            }

            // Read version via a typed IVersioned interface so the cast is
            // compile-time-checked rather than the old dynamic dispatch.
            // TryGet distinguishes "absent" from "present with null"; a false return here means
            // a concurrent DeleteDataAsync raced the Contains check above.
            if (!_state.SagaProvider.TryGet<string, object>(key, out var storedData) || storedData is null)
            {
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {key} was concurrently removed by another saga delete.");
            }

            long currentVersion = storedData is IVersioned versioned
                ? versioned.Version
                : throw new PersistenceException(
                    $"Stored item for CorrelationId {key} is of unexpected type {storedData.GetType()} and does not implement IVersioned.");

            if (currentVersion != newData.Version)
            {
                // Stale-version conflict — ProcessManagerProcessor retries on this type.
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {key} and Version {currentVersion} could not be updated.");
            }

            // Extract the stored Id via IIdentified (analogous to IVersioned above) so
            // the pattern-match is safe even when the stored MemoryData<ConcreteT> does
            // not share a generic parameter with the caller's T (e.g. UpdateDataAsync
            // called with T=IProcessManagerData for a row stored as MemoryData<TestData>).
            Guid existingId = storedData is IIdentified identified
                ? identified.Id
                : Guid.Empty; // unreachable: all stored rows go through BuildMemoryDataFactory which produces MemoryData<T> : IIdentified

            _state.SagaProvider.Update(key, new MemoryData<T>
            {
                // Preserve the stable Id that was stamped at insert — mirrors Mongo's
                // persisted _id which survives every subsequent update to the document.
                Id = existingId,
                // Deep-clone on update so the caller's subsequent mutations do not
                // leak into the stored snapshot. Matches Insert semantics.
                Data = DeepClone.Clone(data.Data),
                Version = newData.Version + 1
            });

            // Reflect the store-side increment back to the caller so consecutive updates
            // using the same MemoryData<T> instance don't fail concurrency check. Mongo
            // persistor returns the post-update document via FindOneAndUpdate; InMemory
            // previously diverged.
            newData.Version++;
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes the stored process manager record identified by the supplied data.
    /// Enforces optimistic concurrency — delete fails with <see cref="ConcurrencyException"/>
    /// if the stored version does not match or the record is missing, matching the update
    /// contract so a delete cannot race an in-flight update and silently drop a saga.
    /// </summary>
    public Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);

        var expected = (MemoryData<T>)data;

        _state.SyncRoot.EnterWriteLock();
        try
        {
            string key = data.Data.CorrelationId.ToString();
            if (!_state.SagaProvider.Contains(key))
            {
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {key} does not exist and cannot be deleted.");
            }

            // TryGet distinguishes "absent" from "present with null"; a false return here means
            // a concurrent DeleteDataAsync raced the Contains check above.
            if (!_state.SagaProvider.TryGet<string, object>(key, out var stored) || stored is null)
            {
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {key} was concurrently removed by another saga delete.");
            }

            long currentVersion = stored is IVersioned versioned
                ? versioned.Version
                : throw new PersistenceException(
                    $"Stored item for CorrelationId {key} is of unexpected type {stored.GetType()} and does not implement IVersioned.");

            if (currentVersion != expected.Version)
            {
                throw new ConcurrencyException(
                    $"Concurrency conflict: ProcessManagerData with CorrelationId {key} and Version {currentVersion} could not be deleted.");
            }

            _state.SagaProvider.Remove(key);
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

}
