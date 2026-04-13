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
#if NET9_0_OR_GREATER
    private readonly Lock _flushLock = new();
#else
    private readonly object _flushLock = new();
#endif

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
            // Cancel any active timer before flushing
            if (_timers.TryRemove(descriptor.AggregatorName, out var timerToCancel))
                timerToCancel.Dispose();
            await FlushAggregatorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        else if (descriptor.Timeout > TimeSpan.Zero)
        {
            lock (_flushLock)
            {
                if (_timers.TryRemove(descriptor.AggregatorName, out var existingTimer))
                    existingTimer.Dispose();

                var timer = new Timer(_ => OnTimerFired(descriptor),
                    null, descriptor.Timeout, Timeout.InfiniteTimeSpan);
                _timers[descriptor.AggregatorName] = timer;
            }
        }

        return ProcessResult.Handled;
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        // If the timer was already removed (batch flush beat us), skip
        if (!_timers.ContainsKey(descriptor.AggregatorName))
            return;

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
        // Dispose the timer under lock, then execute outside lock
        lock (_flushLock)
        {
            if (_timers.TryRemove(descriptor.AggregatorName, out var activeTimer))
                activeTimer.Dispose();
        }

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

    public void Dispose()
    {
        foreach (var kvp in _timers)
            kvp.Value.Dispose();
        _timers.Clear();
    }
}
