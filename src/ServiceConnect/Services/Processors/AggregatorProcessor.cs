using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorProcessor(
    AggregatorRegistry registry,
    IServiceProvider serviceProvider,
    ILogger<AggregatorProcessor> logger,
    IAggregatorPersistor? persistor = null) : IMessageProcessor, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new();
    // Per-aggregator flush lock. Holding this across the full flush body prevents
    // the timer-fired path and the batch-size path from double-flushing and
    // racing on Get/Invoke/Remove.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flushLocks = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<int, Task> _activeFlushes = new();
    private int _flushId;
    private int _disposed;

    public async Task<ProcessResult> ProcessAsync(
        ReadOnlyMemory<byte> messageBytes, Type messageType, object? message,
        IDictionary<string, object> headers, Envelope envelope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message == null) return ProcessResult.NotHandled;

        if (!registry.TryGet(messageType, out var descriptor))
            return ProcessResult.NotHandled;

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

            // Track the batch-path flush so DisposeAsync waits for it to complete. Without
            // this, dispose can race ahead and dispose the flush lock while this thread
            // is mid-flush, yielding ObjectDisposedException.
            var id = Interlocked.Increment(ref _flushId);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeFlushes.TryAdd(id, tcs.Task);
            try
            {
                await FlushAggregatorAsync(descriptor, linkedCts.Token).ConfigureAwait(false);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                throw;
            }
            finally
            {
                _activeFlushes.TryRemove(id, out _);
            }
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
        // causes ObjectDisposedException. The new-timer-per-update pattern is safe
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
        // Use _disposeCts.Token so timer-fired flushes cancel on dispose.
        //
        // Register a TaskCompletionSource in _activeFlushes BEFORE starting the flush
        // so that DisposeAsync's snapshot always includes it.
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

            if (persistor == null) return;

            // Use the snapshot API so we can (a) remove only the specific records we dispatched,
            // leaving concurrently-inserted messages intact (closes the Get/RemoveAll race), and
            // (b) leave unresolved-type records in place instead of silently wiping them.
            var snapshot = await persistor.GetSnapshotAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
            if (snapshot.ResolvedMessages.Count == 0)
            {
                if (snapshot.UnresolvedCount > 0)
                    logger.LogWarning(
                        "Aggregator {AggregatorName} has {UnresolvedCount} record(s) with unresolvable types; skipping dispatch until type is available",
                        descriptor.AggregatorName, snapshot.UnresolvedCount);
                return;
            }

            // BuildTypedList expects IList<object>; wrap the read-only snapshot as a list copy.
            // The snapshot itself stays immutable; the copy is the handler-facing payload.
            var resolvedList = snapshot.ResolvedMessages as IList<object> ?? snapshot.ResolvedMessages.ToList();
            var typedList = descriptor.BuildTypedList(resolvedList);

            var aggregator = serviceProvider.GetService(descriptor.AggregatorBaseType);
            if (aggregator == null) return;

            // Remove BEFORE execute so a cancellation between the two cannot leave the
            // snapshot persisted after the handler has run — that window caused the same
            // batch to be re-fetched and re-dispatched on the next tick. Concurrent inserts
            // and unresolved-type records are preserved; there is still no per-message
            // remove loop. If RemoveSnapshotAsync throws, the awaited InvokeExecuteAsync
            // call is skipped and the batch is retried on the next flush.
            await persistor.RemoveSnapshotAsync(descriptor.AggregatorName, snapshot, cancellationToken).ConfigureAwait(false);

            await descriptor.InvokeExecuteAsync(aggregator, typedList, cancellationToken).ConfigureAwait(false);

            if (snapshot.UnresolvedCount > 0)
                logger.LogWarning(
                    "Aggregator {AggregatorName} dispatched {Count} record(s); {UnresolvedCount} unresolved record(s) retained for a later flush",
                    descriptor.AggregatorName, snapshot.ResolvedMessages.Count, snapshot.UnresolvedCount);
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
