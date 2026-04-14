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
        // Create a new timer on every update instead of reusing via Change().
        // Change() on a timer that FlushAggregatorAsync concurrently TryRemove+Dispose'd
        // causes ObjectDisposedException (I-1). The new-timer-per-update pattern is safe
        // because we dispose the previous timer after AddOrUpdate returns.
        Timer? previous = null;
        _timers.AddOrUpdate(
            descriptor.AggregatorName,
            _ => new Timer(_ => OnTimerFired(descriptor), null, descriptor.Timeout, Timeout.InfiniteTimeSpan),
            (_, existing) =>
            {
                previous = existing;
                return new Timer(_ => OnTimerFired(descriptor), null, descriptor.Timeout, Timeout.InfiniteTimeSpan);
            });
        previous?.Dispose();
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        // Fire and forget from timer callback — log any errors.
        // Use _disposeCts.Token so timer-fired flushes cancel on dispose (R-002).
        //
        // Register a TaskCompletionSource in _activeFlushes BEFORE starting the flush
        // so that DisposeAsync's snapshot always includes it (I-2).
        var id = Interlocked.Increment(ref _flushId);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeFlushes.TryAdd(id, tcs.Task);

        _ = RunFlushAsync(id, tcs, descriptor);
    }

    private async Task RunFlushAsync(int id, TaskCompletionSource tcs, AggregatorDescriptor descriptor)
    {
        try
        {
            await FlushAggregatorAsync(descriptor, _disposeCts.Token).ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            tcs.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error flushing aggregator {AggregatorName} on timeout", descriptor.AggregatorName);
            tcs.TrySetException(ex);
        }
        finally
        {
            _activeFlushes.TryRemove(id, out _);
        }
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
