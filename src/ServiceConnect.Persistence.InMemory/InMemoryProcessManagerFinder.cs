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
public sealed class InMemoryProcessManagerFinder : IProcessManagerFinder
{
    private readonly ProcessManagerPredicateCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryPersistenceState _state;

    public InMemoryProcessManagerFinder(string connectionString, string databaseName)
        : this(new ProcessManagerPredicateCache(), new InMemoryPersistenceState(TimeProvider.System)) { }

    internal InMemoryProcessManagerFinder(ProcessManagerPredicateCache cache, TimeProvider? timeProvider = null)
        : this(cache, new InMemoryPersistenceState(timeProvider), timeProvider) { }

    internal InMemoryProcessManagerFinder(ProcessManagerPredicateCache cache, InMemoryPersistenceState state, TimeProvider? timeProvider = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    private const int InitialVersion = 1;
    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromDays(2);
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (Func<object, object?> Data, Func<object, object?> Version)>
        ReflectionAccessors = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> CloneableProperties = new();
    private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not locate object.MemberwiseClone.");

    public Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(message);

        // Single-pass scan: prefer an exact message-type match, fall back to the base
        // Message wildcard. Previously this used two separate FirstOrDefault calls.
        var exactMessageType = message.GetType();
        ProcessManagerToMessageMap? mapping = null;
        ProcessManagerToMessageMap? fallback = null;
        foreach (var m in mapper.Mappings)
        {
            if (m.MessageType == exactMessageType) { mapping = m; break; }
            if (fallback == null && m.MessageType == typeof(Message)) fallback = m;
        }
        mapping ??= fallback;

        if (mapping == null)
            throw new InvalidOperationException(
                $"No property mapping configured for message type '{message.GetType().FullName}' or the base Message type.");

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
            throw new ArgumentException("Message property expression evaluates to null");

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

    private MemoryData<T>? FindMatchingItem<T>(object msgPropValue, Func<MemoryData<T>, object, bool> predicate)
        where T : class, IProcessManagerData
    {
        foreach (var key in _state.Provider.Keys())
        {
            var value = _state.Provider.Get<string, object>(key.ToString()!);
            if (value is MemoryData<T> typed)
            {
                var candidate = new MemoryData<T> { Data = CloneData(typed.Data), Version = typed.Version };
                if (predicate(candidate, msgPropValue)) return candidate;
            }
            else
            {
                // Support case where data was stored with a different generic parameter
                // (e.g., concrete vs interface T).
                var valueType = value.GetType();
                var accessors = ReflectionAccessors.GetOrAdd(valueType, static type =>
                {
                    var dataProp = type.GetProperty("Data");
                    var versionProp = type.GetProperty("Version");

                    return (
                        dataProp == null ? _ => null : dataProp.GetValue,
                        versionProp == null ? _ => null : versionProp.GetValue);
                });
                
                if (accessors.Data(value) is T typedData && accessors.Version(value) is int version)
                {
                    var candidate = new MemoryData<T> { Data = CloneData(typedData), Version = version };
                    if (predicate(candidate, msgPropValue)) return candidate;
                }
            }
        }
        return null;
    }

    private static T CloneData<T>(T data)
        where T : class, IProcessManagerData
    {
        var runtimeType = data.GetType();
        var clone = MemberwiseCloneMethod.Invoke(data, null)
            ?? throw new PersistenceException($"Failed to clone process manager data of type '{runtimeType.FullName}'.");

        var properties = CloneableProperties.GetOrAdd(runtimeType, static type =>
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.CanWrite)
                .ToArray());

        foreach (var property in properties)
        {
            var value = property.GetValue(data);
            property.SetValue(clone, value is byte[] bytes ? (byte[])bytes.Clone() : value);
        }

        return (T)clone;
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
                left = Expression.Property(left, left.Type, prop.Key);
            }

            Expression right = Expression.Convert(valueParam, key.PropertyType);
            var eq = Expression.Equal(left, right);

            var delegateType = typeof(Func<,,>).MakeGenericType(dataParam.Type, typeof(object), typeof(bool));
            return Expression.Lambda(delegateType, eq, dataParam, valueParam).Compile();
        });
        return (Func<MemoryData<T>, object, bool>)compiled;
    }

    public Task InsertDataAsync(IProcessManagerData data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);

        var factory = _cache.MemoryDataFactories.GetOrAdd(data.GetType(), BuildMemoryDataFactory);
        var memoryData = factory(data);

        _state.SyncRoot.EnterWriteLock();
        try
        {
            string key = data.CorrelationId.ToString();
            if (_state.Provider.Contains(key))
                throw new PersistenceException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");

            _state.Provider.Add(key, memoryData, _timeProvider.GetUtcNow().Add(ExpiryDuration));
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    // One-time compiled factory per concrete data type. Replaces the previous
    // GetType().GetMethods().First(...) + MakeGenericMethod + Invoke per call.
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

    public Task UpdateDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            string? error = null;
            var newData = (MemoryData<T>)data;
            string key = data.Data.CorrelationId.ToString();

            if (_state.Provider.Contains(key))
            {
                // Read version via a typed IVersioned interface so the cast is
                // compile-time-checked rather than the old dynamic dispatch.
                var storedData = _state.Provider.Get<string, object>(key);
                int currentVersion = storedData is IVersioned versioned
                    ? versioned.Version
                    : throw new PersistenceException(
                        $"Stored item for CorrelationId {key} is of unexpected type {storedData.GetType()} and does not implement IVersioned.");

                var updatedData = new MemoryData<T>
                {
                    Data = data.Data,
                    Version = newData.Version + 1
                };

                if (currentVersion == newData.Version)
                {
                    _state.Provider.Update(key, updatedData);
                }
                else
                {
                    error = $"Possible Concurrency Error. ProcessManagerData with CorrelationId {key} and Version {currentVersion} could not be updated.";
                }
            }
            else
            {
                error = $"ProcessManagerData with CorrelationId {key} does not exist in memory.";
            }

            if (!string.IsNullOrEmpty(error))
                throw new PersistenceException(error);
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

    public Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        _state.SyncRoot.EnterWriteLock();
        try
        {
            string key = data.Data.CorrelationId.ToString();
            _state.Provider.Remove(key);
        }
        finally
        {
            _state.SyncRoot.ExitWriteLock();
        }

        return Task.CompletedTask;
    }

}
