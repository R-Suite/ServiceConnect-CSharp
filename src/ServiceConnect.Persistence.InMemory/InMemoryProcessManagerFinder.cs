using System.Linq.Expressions;
using System.Reflection;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Exceptions;

namespace ServiceConnect.Persistence.InMemory;

/// <summary>
/// InMemory implementation of IProcessManagerFinder for testing and rapid development
/// </summary>
public sealed class InMemoryProcessManagerFinder : IProcessManagerFinder, ITimeoutStore
{
    // Parameters required by IProcessManagerFinder factory convention but unused in InMemory implementation
    public InMemoryProcessManagerFinder(string connectionString, string databaseName) { }
#if NET9_0_OR_GREATER
    private readonly Lock _memoryCacheLock = new();
#else
    private readonly object _memoryCacheLock = new();
#endif

    private const int InitialVersion = 1;
    private static readonly TimeSpan ExpiryDuration = TimeSpan.FromDays(2);
    private static readonly TimeSpan DefaultNextQueryInterval = TimeSpan.FromMinutes(1);
    private CacheProvider _provider = new();

    public event TimeoutInsertedDelegate? TimeoutInserted;

    public IPersistenceData<T>? FindData<T>(IProcessManagerPropertyMapper mapper, Message message) where T : class, IProcessManagerData
    {
        lock (_memoryCacheLock)
        {
            var mapping = mapper.Mappings.FirstOrDefault(m => m.MessageType == message.GetType())
                      ?? mapper.Mappings.FirstOrDefault(m => m.MessageType == typeof(Message));

            if (mapping == null)
                throw new InvalidOperationException(
                    $"No property mapping configured for message type '{message.GetType().FullName}' or the base Message type.");

            object? msgPropValue = null;

            try
            {
                msgPropValue = mapping.MessageProp.Invoke(message);
            }
            catch (Exception)
            {
                // Property mapping invocation failed — no matching data for this message
                return null;
            }

            if (msgPropValue is null)
            {
                throw new ArgumentException("Message property expression evaluates to null");
            }

            //Left
            ParameterExpression pe = Expression.Parameter(typeof(MemoryData<T>), "t");
            Expression left = Expression.Property(pe, typeof(MemoryData<T>).GetTypeInfo().GetProperty("Data")!);
            foreach (var prop in mapping.PropertiesHierarchy.Reverse())
            {
                left = Expression.Property(left, left.Type, prop.Key);
            }

            //Right
            Expression right = Expression.Constant(msgPropValue, msgPropValue.GetType());

            Expression expression;

            try
            {
                expression = Expression.Equal(left, right);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("Mapped incompatible types of ProcessManager Data and Message properties.", ex);
            }

            Expression<Func<MemoryData<T>, bool>> lambda = Expression.Lambda<Func<MemoryData<T>, bool>>(expression, pe);

            var newCacheItems = new List<MemoryData<T>>();

            foreach (var key in _provider.Keys())
            {
                var value = _provider.Get<string, object>(key.ToString()!);
                if (value.GetType() == typeof(MemoryData<T>))
                {
                    var typed = (MemoryData<T>)value;
                    newCacheItems.Add(new MemoryData<T> { Data = typed.Data, Version = typed.Version });
                }
                else
                {
                    // Support case where data was stored with a different generic parameter
                    // (e.g., stored as MemoryData<ConcreteType>, queried as MemoryData<IProcessManagerData>)
                    var valueType = value.GetType();
                    var dataProp = valueType.GetProperty("Data");
                    var versionProp = valueType.GetProperty("Version");
                    if (dataProp != null && versionProp != null)
                    {
                        var data = dataProp.GetValue(value);
                        if (data is T typedData)
                        {
                            newCacheItems.Add(new MemoryData<T> { Data = typedData, Version = (int)versionProp.GetValue(value)! });
                        }
                    }
                }
            }

            MemoryData<T>? retval = newCacheItems.FirstOrDefault(lambda.Compile());

            return retval;
        }
    }

    public void InsertData(IProcessManagerData data)
    {
        Type typeParameterType = data.GetType();

        MethodInfo md = GetType().GetTypeInfo().GetMethods().First(m => m.Name == "GetMemoryData" && m.GetParameters()[0].Name == "data");
        MethodInfo genericMd = md.MakeGenericMethod(typeParameterType);

        lock (_memoryCacheLock)
        {
            var memoryData = genericMd.Invoke(this, [data]);

            string key = data.CorrelationId.ToString();

            if (!_provider.Contains(key))
            {
                _provider.Add(key, memoryData!, DateTime.UtcNow.Add(ExpiryDuration));
            }
            else
            {
                throw new PersistenceException($"ProcessManagerData with CorrelationId {key} already exists in the cache.");
            }
        }
    }

    public MemoryData<DT> GetMemoryData<DT>(DT data) where DT : class, IProcessManagerData
    {
        var memoryData = new MemoryData<DT>
        {
            Data = data,
            Version = InitialVersion,
            Id = Guid.NewGuid()
        };

        return memoryData;
    }

    public void UpdateData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData
    {
        lock (_memoryCacheLock)
        {
            string? error = null;
            var newData = (MemoryData<T>)data;
            string key = data.Data.CorrelationId.ToString();

            if (_provider.Contains(key))
            {
                // Use dynamic to read the version, since the stored MemoryData<X>
                // generic parameter may differ from T (e.g., concrete vs interface).
                dynamic storedData = _provider.Get<string, object>(key);
                int currentVersion = (int)storedData.Version;

                var updatedData = new MemoryData<T>
                {
                    Data = data.Data,
                    Version = newData.Version + 1
                };

                if (currentVersion == newData.Version)
                {
                    _provider.Remove(key);
                    _provider.Add(key, updatedData, DateTime.UtcNow.Add(ExpiryDuration));
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
            {
                throw new PersistenceException(error);
            }
        }
    }

    public void DeleteData<T>(IPersistenceData<T> data) where T : class, IProcessManagerData
    {
        lock (_memoryCacheLock)
        {
            string key = data.Data.CorrelationId.ToString();
            _provider.Remove(key);
        }
    }

    public void InsertTimeout(TimeoutData timeoutData)
    {
        lock (_memoryCacheLock)
        {
            string key = timeoutData.Id.ToString();

            if (!_provider.Contains(key))
            {
                _provider.Add(key, timeoutData, DateTime.UtcNow.Add(ExpiryDuration));
            }
            else
            {
                throw new PersistenceException($"TimeoutData with Id {key} already exists in the cache.");
            }
        }

        TimeoutInserted?.Invoke(timeoutData.Time);
    }

    public TimeoutsBatch GetTimeoutsBatch()
    {
        var retval = new TimeoutsBatch { DueTimeouts = [] };

        DateTime utcNow = DateTime.UtcNow;

        var nextQueryTime = DateTime.MaxValue;

        lock (_memoryCacheLock)
        {
            Dictionary<string, object> cacheItems = new Dictionary<string, object>();

            foreach (var key in _provider.Keys())
            {
                var value = _provider.Get<string, object>(key.ToString()!);
                if (value.GetType() == typeof(TimeoutData))
                {
                    cacheItems.Add(key.ToString()!, value);
                }
            }

            foreach (var data in cacheItems)
            {
                var timeoutData = (TimeoutData)data.Value;
                if (timeoutData.Time <= utcNow)
                {
                    retval.DueTimeouts.Add(timeoutData);
                }

                if (timeoutData.Time > utcNow && timeoutData.Time < nextQueryTime)
                {
                    nextQueryTime = timeoutData.Time;
                }
            }
        }

        if (nextQueryTime == DateTime.MaxValue)
        {
            nextQueryTime = utcNow.Add(DefaultNextQueryInterval);
        }

        retval.NextQueryTime = nextQueryTime;

        return retval;
    }

    public void RemoveDispatchedTimeout(Guid id)
    {
        lock (_memoryCacheLock)
        {
            _provider.Remove(id.ToString());
        }
    }
}
