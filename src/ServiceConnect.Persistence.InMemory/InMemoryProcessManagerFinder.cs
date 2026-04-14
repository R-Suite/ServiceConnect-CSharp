using System.Collections.Concurrent;
using System.Linq.Expressions;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// InMemory implementation of IProcessManagerFinder for testing and rapid development.
/// Compiled predicates are cached by mapping shape so correlation lookups avoid both
/// Expression.Compile and reflection on the hot path (A-05).
/// </summary>
public sealed class InMemoryProcessManagerFinder : IProcessManagerFinder, ITimeoutStore
{
    public InMemoryProcessManagerFinder(string connectionString, string databaseName) { }

#if NET9_0_OR_GREATER
    private readonly Lock _memoryCacheLock = new();
#else
    private readonly object _memoryCacheLock = new();
#endif

    private const int InitialVersion = 1;
    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromDays(2);
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
    private readonly CacheProvider _provider = new();

    // Cached predicates of shape (MemoryData<T>, object) -> bool. Key is the mapping shape.
    private static readonly ConcurrentDictionary<PredicateCacheKey, Delegate> CompiledPredicates = new();

    // Cached factories that produce MemoryData<TConcrete> from IProcessManagerData, keyed by concrete type.
    private static readonly ConcurrentDictionary<Type, Func<IProcessManagerData, object>> MemoryDataFactories = new();

    public Task<IPersistenceData<T>?> FindDataAsync<T>(IProcessManagerPropertyMapper mapper, Message message, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(message);

        // Single-pass scan: prefer an exact message-type match, fall back to the base
        // Message wildcard. Previously two separate FirstOrDefault calls (P-046).
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

        lock (_memoryCacheLock)
        {
            return Task.FromResult<IPersistenceData<T>?>(FindMatchingItem<T>(msgPropValue, predicate));
        }
    }

    private MemoryData<T>? FindMatchingItem<T>(object msgPropValue, Func<MemoryData<T>, object, bool> predicate)
        where T : class, IProcessManagerData
    {
        foreach (var key in _provider.Keys())
        {
            var value = _provider.Get<string, object>(key.ToString()!);
            if (value is MemoryData<T> typed)
            {
                var candidate = new MemoryData<T> { Data = typed.Data, Version = typed.Version };
                if (predicate(candidate, msgPropValue)) return candidate;
            }
            else
            {
                // Support case where data was stored with a different generic parameter
                // (e.g., concrete vs interface T).
                var valueType = value.GetType();
                var dataProp = valueType.GetProperty("Data");
                var versionProp = valueType.GetProperty("Version");
                if (dataProp?.GetValue(value) is T typedData && versionProp != null)
                {
                    var candidate = new MemoryData<T> { Data = typedData, Version = (int)versionProp.GetValue(value)! };
                    if (predicate(candidate, msgPropValue)) return candidate;
                }
            }
        }
        return null;
    }

    private static Func<MemoryData<T>, object, bool> GetPredicate<T>(
        IReadOnlyDictionary<string, Type> propertiesHierarchy, Type propertyType)
        where T : class, IProcessManagerData
    {
        var cacheKey = new PredicateCacheKey(typeof(T), propertiesHierarchy, propertyType);
        var compiled = CompiledPredicates.GetOrAdd(cacheKey, static key =>
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

        var factory = MemoryDataFactories.GetOrAdd(data.GetType(), BuildMemoryDataFactory);
        var memoryData = factory(data);

        lock (_memoryCacheLock)
        {
            string key = data.CorrelationId.ToString();
            if (_provider.Contains(key))
                throw new PersistenceException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");

            _provider.Add(key, memoryData, DateTime.UtcNow.Add(ExpiryDuration));
        }

        return Task.CompletedTask;
    }

    // One-time compiled factory per concrete data type. Replaces the previous
    // GetType().GetMethods().First(...) + MakeGenericMethod + Invoke per call (A-05).
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

        lock (_memoryCacheLock)
        {
            string? error = null;
            var newData = (MemoryData<T>)data;
            string key = data.Data.CorrelationId.ToString();

            if (_provider.Contains(key))
            {
                // Read version via a typed IVersioned interface so the cast is
                // compile-time-checked rather than the old dynamic dispatch (A-05).
                var storedData = _provider.Get<string, object>(key);
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
                    _provider.Update(key, updatedData);
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

        return Task.CompletedTask;
    }

    public Task DeleteDataAsync<T>(IPersistenceData<T> data, CancellationToken cancellationToken = default) where T : class, IProcessManagerData
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_memoryCacheLock)
        {
            string key = data.Data.CorrelationId.ToString();
            _provider.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task InsertTimeoutAsync(TimeoutData timeoutData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_memoryCacheLock)
        {
            string key = timeoutData.Id.ToString();

            if (_provider.Contains(key))
                throw new PersistenceException($"TimeoutData with Id {key} already exists in the cache.");

            _provider.Add(key, timeoutData, DateTime.UtcNow.Add(ExpiryDuration));
        }

        return Task.CompletedTask;
    }

    public Task<TimeoutsBatch> GetTimeoutsBatchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var retval = new TimeoutsBatch { DueTimeouts = [] };
        DateTime utcNow = DateTime.UtcNow;
        var nextQueryTime = DateTime.MaxValue;

        lock (_memoryCacheLock)
        {
            foreach (var key in _provider.Keys())
            {
                var value = _provider.Get<string, object>(key.ToString()!);
                if (value is TimeoutData timeoutData)
                {
                    if (timeoutData.Time <= utcNow)
                        retval.DueTimeouts.Add(timeoutData);
                    else if (timeoutData.Time < nextQueryTime)
                        nextQueryTime = timeoutData.Time;
                }
            }
        }

        if (nextQueryTime == DateTime.MaxValue)
            nextQueryTime = utcNow.Add(DefaultNextQueryInterval);

        retval.NextQueryTime = nextQueryTime;
        return Task.FromResult(retval);
    }

    public Task RemoveDispatchedTimeoutAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_memoryCacheLock)
        {
            _provider.Remove(id.ToString());
        }

        return Task.CompletedTask;
    }

    private readonly struct PredicateCacheKey : IEquatable<PredicateCacheKey>
    {
        public readonly Type T;
        public readonly IReadOnlyDictionary<string, Type> PropertiesHierarchy;
        public readonly Type PropertyType;

        public PredicateCacheKey(Type t, IReadOnlyDictionary<string, Type> propertiesHierarchy, Type propertyType)
        {
            T = t;
            PropertiesHierarchy = propertiesHierarchy;
            PropertyType = propertyType;
        }

        public bool Equals(PredicateCacheKey other)
        {
            if (T != other.T || PropertyType != other.PropertyType) return false;
            if (PropertiesHierarchy.Count != other.PropertiesHierarchy.Count) return false;
            foreach (var kvp in PropertiesHierarchy)
            {
                if (!other.PropertiesHierarchy.TryGetValue(kvp.Key, out var otherType) || otherType != kvp.Value)
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is PredicateCacheKey k && Equals(k);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(T);
            hash.Add(PropertyType);
            // XOR-combine per-entry hashes so the result is independent of the
            // dictionary's (undefined) iteration order (M-2). Otherwise Equals
            // could be true while GetHashCode disagreed, violating the contract.
            int entryHash = 0;
            foreach (var kvp in PropertiesHierarchy)
                entryHash ^= HashCode.Combine(kvp.Key, kvp.Value);
            hash.Add(entryHash);
            return hash.ToHashCode();
        }
    }
}
