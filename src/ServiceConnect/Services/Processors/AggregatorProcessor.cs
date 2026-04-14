using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorProcessor(
    AggregatorRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<AggregatorProcessor> logger) : IMessageProcessor, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    // Per-aggregator flush lock. Holding this across the full flush body prevents
    // the timer-fired path and the batch-size path from double-flushing and
    // racing on Get/Invoke/Remove (R-039 / C-01).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flushLocks = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<int, Task> _activeFlushes = new();
    private int _flushId;
    private int _disposed;

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
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            await FlushAggregatorAsync(descriptor, linkedCts.Token).ConfigureAwait(false);
        }
        else if (descriptor.Timeout > TimeSpan.Zero)
        {
            ResetTimer(descriptor);
        }

        return ProcessResult.Handled;
    }

    private void ResetTimer(AggregatorDescriptor descriptor)
    {
        // Reuse existing timer via Change() instead of allocating a new Timer per message (R-028 / P-013).
        // Only create a new timer on first use for a given aggregator name.
        _timers.AddOrUpdate(
            descriptor.AggregatorName,
            _ => new Timer(_ => OnTimerFired(descriptor), null, descriptor.Timeout, Timeout.InfiniteTimeSpan),
            (_, existing) =>
            {
                existing.Change(descriptor.Timeout, Timeout.InfiniteTimeSpan);
                return existing;
            });
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        // Fire and forget from timer callback — log any errors.
        // Use _disposeCts.Token so timer-fired flushes cancel on dispose (R-002).
        var id = Interlocked.Increment(ref _flushId);
        var task = FlushAggregatorAsync(descriptor, _disposeCts.Token);
        _activeFlushes.TryAdd(id, task);
        _ = task.ContinueWith(_ => _activeFlushes.TryRemove(id, out var _ignored), TaskContinuationOptions.ExecuteSynchronously);
        _ = task.ContinueWith(t =>
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

            // R-001: Atomic bulk remove instead of per-message loop to prevent double-processing.
            await persistor.RemoveAllAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Signal all in-flight flushes to cancel.
        _disposeCts.Cancel();

        // Await all tracked pending flushes to complete or cancel.
        var pending = _activeFlushes.Values.ToArray();
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — we just cancelled it.
            }
            catch (ObjectDisposedException)
            {
                // Semaphore may have been disposed during cancellation.
            }
        }

        foreach (var kvp in _timers)
            kvp.Value.Dispose();
        _timers.Clear();

        foreach (var kvp in _flushLocks)
            kvp.Value.Dispose();
        _flushLocks.Clear();

        _disposeCts.Dispose();
    }
}
