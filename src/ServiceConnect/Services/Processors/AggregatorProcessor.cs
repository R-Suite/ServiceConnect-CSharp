using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorProcessor(
    AggregatorRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<AggregatorProcessor> logger) : IMessageProcessor, IDisposable
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    // Per-aggregator flush lock. Holding this across the full flush body prevents
    // the timer-fired path and the batch-size path from double-flushing and
    // racing on Get/Invoke/Remove (R-039 / C-01).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flushLocks = new();
    private volatile bool _disposed;

    public async Task<ProcessResult> ProcessAsync(
        byte[] messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        if (!registry.TryGet(messageType, out var descriptor))
            return ProcessResult.NotHandled;

        var persistor = serviceProvider.GetService<IAggregatorPersistor>();
        if (persistor == null)
        {
            logger.LogWarning("IAggregatorPersistor not registered. Cannot aggregate {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        await persistor.InsertDataAsync(message, descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);

        var count = await persistor.CountAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
        if (descriptor.BatchSize > 0 && count >= descriptor.BatchSize)
        {
            await FlushAggregatorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        else if (descriptor.Timeout > TimeSpan.Zero)
        {
            ResetTimer(descriptor);
        }

        return ProcessResult.Handled;
    }

    private void ResetTimer(AggregatorDescriptor descriptor)
    {
        // Replace any in-flight timer with a fresh one and dispose the old one.
        var timer = new Timer(_ => OnTimerFired(descriptor),
            null, descriptor.Timeout, Timeout.InfiniteTimeSpan);
        if (_timers.TryGetValue(descriptor.AggregatorName, out var existing))
        {
            _timers[descriptor.AggregatorName] = timer;
            existing.Dispose();
        }
        else
        {
            _timers[descriptor.AggregatorName] = timer;
        }
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        // Fire and forget from timer callback — log any errors
        _ = FlushAggregatorAsync(descriptor, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogError(t.Exception, "Error flushing aggregator {AggregatorName} on timeout", descriptor.AggregatorName);
            }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task FlushAggregatorAsync(AggregatorDescriptor descriptor, CancellationToken cancellationToken)
    {
        var flushLock = _flushLocks.GetOrAdd(descriptor.AggregatorName, _ => new SemaphoreSlim(1, 1));
        await flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_timers.TryRemove(descriptor.AggregatorName, out var activeTimer))
                activeTimer.Dispose();

            var persistor = serviceProvider.GetService<IAggregatorPersistor>();
            if (persistor == null) return;

            var rawMessages = await persistor.GetDataAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
            if (rawMessages.Count == 0) return;

            var typedList = descriptor.BuildTypedList(rawMessages);

            var aggregator = serviceProvider.GetService(descriptor.AggregatorBaseType);
            if (aggregator == null) return;

            descriptor.InvokeExecute(aggregator, typedList);

            foreach (var msg in rawMessages)
            {
                if (msg is Message m)
                    await persistor.RemoveDataAsync(descriptor.AggregatorName, m.CorrelationId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            flushLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kvp in _timers)
            kvp.Value.Dispose();
        _timers.Clear();
        foreach (var kvp in _flushLocks)
            kvp.Value.Dispose();
        _flushLocks.Clear();
    }
}
