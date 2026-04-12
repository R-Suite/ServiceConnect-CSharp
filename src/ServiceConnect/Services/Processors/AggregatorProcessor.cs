using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

public sealed class AggregatorProcessor(IServiceProvider serviceProvider, ILogger<AggregatorProcessor> logger) : IMessageProcessor, IDisposable
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
#if NET9_0_OR_GREATER
    private readonly Lock _flushLock = new();
#else
    private readonly object _flushLock = new();
#endif

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope)
    {
        if (message == null) return ProcessResult.NotHandled;

        var aggregatorBaseType = FindAggregatorType(messageType);
        if (aggregatorBaseType == null)
            return ProcessResult.NotHandled;

        var persistor = serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null)
        {
            logger.LogWarning("IAggregatorPersistor not registered. Cannot aggregate {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        var aggregator = serviceProvider.GetService(aggregatorBaseType);
        if (aggregator == null) return ProcessResult.NotHandled;

        var aggregatorName = aggregatorBaseType.FullName!;
        var batchSize = (int)aggregatorBaseType.GetMethod("BatchSize")!.Invoke(aggregator, null)!;
        var timeout = (TimeSpan)aggregatorBaseType.GetMethod("Timeout")!.Invoke(aggregator, null)!;

        persistor.InsertData(message, aggregatorName);

        var count = persistor.Count(aggregatorName);
        if (batchSize > 0 && count >= batchSize)
        {
            // Cancel any active timer before flushing
            if (_timers.TryRemove(aggregatorName, out var timerToCancel))
            {
                timerToCancel.Dispose();
            }
            FlushAggregator(aggregatorName, messageType, aggregatorBaseType);
        }
        else if (timeout > TimeSpan.Zero)
        {
            lock (_flushLock)
            {
                if (_timers.TryRemove(aggregatorName, out var existingTimer))
                    existingTimer.Dispose();

                var timer = new Timer(_ => OnTimerFired(aggregatorName, messageType, aggregatorBaseType),
                    null, timeout, Timeout.InfiniteTimeSpan);
                _timers[aggregatorName] = timer;
            }
        }

        return ProcessResult.Handled;
    }

    private void OnTimerFired(string aggregatorName, Type messageType, Type aggregatorBaseType)
    {
        // If the timer was already removed (batch flush beat us), skip
        if (!_timers.ContainsKey(aggregatorName))
            return;

        try
        {
            FlushAggregator(aggregatorName, messageType, aggregatorBaseType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error flushing aggregator {AggregatorName} on timeout", aggregatorName);
        }
    }

    private void FlushAggregator(string aggregatorName, Type messageType, Type aggregatorBaseType)
    {
        IList<object> rawMessages;
        System.Collections.IList typedList;
        object? aggregator;
        System.Reflection.MethodInfo? executeMethod;

        lock (_flushLock)
        {
            var persistor = serviceProvider.GetService<IAggregatorPersistor>();
            if (persistor == null) return;

            rawMessages = persistor.GetData(aggregatorName);
            if (rawMessages.Count == 0) return;

            var listType = typeof(List<>).MakeGenericType(messageType);
            typedList = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var msg in rawMessages)
                typedList.Add(msg);

            aggregator = serviceProvider.GetService(aggregatorBaseType);
            if (aggregator == null) return;

            executeMethod = aggregatorBaseType.GetMethod("Execute");

            if (_timers.TryRemove(aggregatorName, out var activeTimer))
                activeTimer.Dispose();
        }

        executeMethod?.Invoke(aggregator, [typedList]);

        var postFlushPersistor = serviceProvider.GetService<IAggregatorPersistor>();
        if (postFlushPersistor != null)
        {
            foreach (var msg in rawMessages)
            {
                if (msg is Message m)
                    postFlushPersistor.RemoveData(aggregatorName, m.CorrelationId);
            }
        }
    }

    private Type? FindAggregatorType(Type messageType)
    {
        var handlerRefs = serviceProvider.GetService<IList<HandlerReference>>();
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
