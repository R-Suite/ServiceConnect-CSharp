using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public class AggregatorProcessor : IMessageProcessor, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AggregatorProcessor> _logger;
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    private readonly object _flushLock = new();

    public AggregatorProcessor(IServiceProvider serviceProvider, ILogger<AggregatorProcessor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        var aggregatorBaseType = FindAggregatorType(messageType);
        if (aggregatorBaseType == null)
            return ProcessResult.NotHandled;

        var persistor = _serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null)
        {
            _logger.LogWarning("IAggregatorPersistor not registered. Cannot aggregate {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        var aggregator = _serviceProvider.GetService(aggregatorBaseType);
        if (aggregator == null) return ProcessResult.NotHandled;

        var aggregatorName = aggregatorBaseType.FullName!;
        var batchSize = (int)aggregatorBaseType.GetMethod("BatchSize")!.Invoke(aggregator, null)!;
        var timeout = (TimeSpan)aggregatorBaseType.GetMethod("Timeout")!.Invoke(aggregator, null)!;

        persistor.InsertData(message, aggregatorName);

        var count = persistor.Count(aggregatorName);
        if (batchSize > 0 && count >= batchSize)
        {
            FlushAggregator(aggregatorName, messageType, aggregatorBaseType);
        }
        else if (timeout > TimeSpan.Zero)
        {
            if (_timers.TryRemove(aggregatorName, out var existingTimer))
                existingTimer.Dispose();

            var timer = new Timer(_ => OnTimerFired(aggregatorName, messageType, aggregatorBaseType),
                null, timeout, System.Threading.Timeout.InfiniteTimeSpan);
            _timers[aggregatorName] = timer;
        }

        return ProcessResult.Handled;
    }

    private void OnTimerFired(string aggregatorName, Type messageType, Type aggregatorBaseType)
    {
        try
        {
            FlushAggregator(aggregatorName, messageType, aggregatorBaseType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error flushing aggregator {AggregatorName} on timeout", aggregatorName);
        }
        finally
        {
            if (_timers.TryRemove(aggregatorName, out var timer))
                timer.Dispose();
        }
    }

    private void FlushAggregator(string aggregatorName, Type messageType, Type aggregatorBaseType)
    {
        lock (_flushLock)
        {
            var persistor = _serviceProvider.GetService<IAggregatorPersistor>();
            if (persistor == null) return;

            var rawMessages = persistor.GetData(aggregatorName);
            if (rawMessages.Count == 0) return;

            var listType = typeof(List<>).MakeGenericType(messageType);
            var typedList = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var msg in rawMessages)
                typedList.Add(msg);

            var aggregator = _serviceProvider.GetService(aggregatorBaseType);
            if (aggregator == null) return;

            var executeMethod = aggregatorBaseType.GetMethod("Execute");
            executeMethod?.Invoke(aggregator, new object[] { typedList });

            foreach (var msg in rawMessages)
            {
                if (msg is Message m)
                    persistor.RemoveData(aggregatorName, m.CorrelationId);
            }

            if (_timers.TryRemove(aggregatorName, out var activeTimer))
                activeTimer.Dispose();
        }
    }

    private Type? FindAggregatorType(Type messageType)
    {
        var handlerRefs = _serviceProvider.GetService<IList<HandlerReference>>();
        if (handlerRefs == null) return null;

        foreach (var href in handlerRefs)
        {
            if (href.MessageType != messageType) continue;
            if (href.HandlerType.BaseType is { IsGenericType: true } baseType &&
                baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
            {
                return baseType;
            }
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var kvp in _timers)
            kvp.Value.Dispose();
        _timers.Clear();
    }
}
