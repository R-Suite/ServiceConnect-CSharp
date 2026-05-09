using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services.Processors;

internal sealed class AggregatorProcessor(
    AggregatorRegistry registry,
    ConsumeScopeAccessor scopeAccessor,
    IServiceScopeFactory scopeFactory,
    ILogger<AggregatorProcessor> logger,
    IAggregatorPersistor? persistor = null) : IMessageProcessor, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    // Single-flight lock for ResetTimer. Concurrent calls for the same aggregator
    // would otherwise rely on ConcurrentDictionary.AddOrUpdate factory semantics,
    // whose factory may re-run under contention — losing-factory Timer instances
    // are then orphaned (already running, never installed, never disposed).
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _resetTimerLock = new();
#else
    private readonly object _resetTimerLock = new();
#endif
    // Per-aggregator flush lock. Holding this across the full flush body prevents
    // the timer-fired path and the batch-size path from double-flushing and
    // racing on Get/Invoke/Remove.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flushLocks = new(StringComparer.Ordinal);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (message == null)
        {
            return ProcessResult.NotHandled;
        }

        if (!registry.TryGet(messageType, out var descriptor))
        {
            return ProcessResult.NotHandled;
        }

        if (persistor == null)
        {
            logger.LogWarning("IAggregatorPersistor not registered. Cannot aggregate {MessageType}", messageType.FullName);
            return ProcessResult.NotHandled;
        }

        // IAggregatorPersistor.InsertDataAsync requires IHasCorrelationId. All aggregatable
        // message types must implement it; Message does so automatically. Non-implementers are
        // rejected here rather than at the persistor boundary so the error surfaces at the
        // processor level with a clear message.
        if (message is not IHasCorrelationId withCorrId)
        {
            logger.LogWarning(
                "Message type '{MessageType}' does not implement IHasCorrelationId; cannot aggregate",
                messageType.FullName);
            return ProcessResult.NotHandled;
        }

        await persistor.InsertDataAsync(withCorrId, descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);

        // Use CountResolvedAsync so unresolved-only batches don't trigger empty flushes.
        // Pre-fix the gate consulted CountAsync (total rows) and an unresolved-only batch
        // would fire the gate on every message — flush returned no-op (ResolvedMessages.Count
        // == 0) but each iteration still acquired the per-aggregator semaphore and made a
        // GetSnapshotAsync round-trip. CountResolvedAsync is the cheap shape on first-party
        // persistors; the interface default delegates to GetSnapshotAsync for back-compat
        // with third-party implementations.
        var count = await persistor.CountResolvedAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
        if (descriptor.BatchSize > 0 && count >= descriptor.BatchSize)
        {
            // Register in _activeFlushes BEFORE reading _disposeCts.Token. A concurrent
            // DisposeAsync either (a) takes its drain snapshot before our TryAdd — we
            // re-check _disposed below and bail with ODE; or (b) sees our entry in the
            // snapshot and awaits its completion. Either way _disposeCts.Token is only
            // ever read while DisposeAsync is still awaiting our task.
            var id = Interlocked.Increment(ref _flushId);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeFlushes.TryAdd(id, tcs.Task);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
                await FlushAggregatorAsync(descriptor, scopeAccessor.Current, linkedCts.Token).ConfigureAwait(false);
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
        Timer? previous;
        Timer newTimer;
        lock (_resetTimerLock)
        {
            // Re-check _disposed under the same lock that DisposeAsync's timer cleanup
            // takes. Without this check a ProcessAsync that passed the entry guard at
            // ProcessAsync line 41 can land here after DisposeAsync cleared _timers and
            // install a fresh Timer that nobody disposes (bounded leak per aggregator-name).
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _timers.TryGetValue(descriptor.AggregatorName, out previous);
            newTimer = new Timer(_ => OnTimerFired(descriptor), null, descriptor.Timeout, Timeout.InfiniteTimeSpan);
            _timers[descriptor.AggregatorName] = newTimer;
        }
        previous?.Dispose();
    }

    private void OnTimerFired(AggregatorDescriptor descriptor)
    {
        // Register the TaskCompletionSource in _activeFlushes BEFORE consulting _disposed
        // so that DisposeAsync's snapshot at _activeFlushes.Values.ToArray() is guaranteed
        // to either (a) include our entry — DisposeAsync awaits it — or (b) take its snapshot
        // AFTER we observe _disposed and bail.
        //
        // The pre-fix order (read _disposed, then TryAdd) had a window where DisposeAsync
        // could set _disposed=1 between our read and the snapshot; the snapshot would miss
        // our entry; DisposeAsync would dispose _disposeCts; and RunFlushAsync's defensive
        // catch (ObjectDisposedException) softened the failure to quiet cancellation.
        var id = Interlocked.Increment(ref _flushId);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeFlushes.TryAdd(id, tcs.Task);

        // Re-check after registration. If DisposeAsync's Exchange(_disposed,1) ran before
        // our TryAdd, our entry was missed by the drain snapshot — complete the tcs as
        // cancelled and remove it so we don't leak the registration past dispose.
        if (Volatile.Read(ref _disposed) != 0)
        {
            tcs.TrySetCanceled();
            _activeFlushes.TryRemove(id, out _);
            return;
        }

        // Fire and forget from timer callback — log any errors.
        // Use _disposeCts.Token via the same shutdown-race-tolerant read in RunFlushAsync.
        _ = RunFlushAsync(id, tcs, descriptor);
    }

    private async Task RunFlushAsync(int id, TaskCompletionSource tcs, AggregatorDescriptor descriptor)
    {
        // OnTimerFired's `_disposed` guard and this method's `_disposeCts.Token` read
        // aren't atomic: a callback that passed the guard at T1 can still reach here
        // after DisposeAsync has disposed `_disposeCts`. Wrap the Token read so the
        // shutdown race surfaces as quiet cancellation instead of a spurious ERROR log.
        CancellationToken token;
        try
        {
            token = _disposeCts.Token;
        }
        catch (ObjectDisposedException)
        {
            tcs.TrySetCanceled();
            _activeFlushes.TryRemove(id, out _);
            return;
        }

        try
        {
            // Pass null for ambientScope so FlushAggregatorAsync always creates a fresh DI
            // scope. The Timer captured the dispatcher's ExecutionContext (and therefore
            // the AsyncLocal-backed ConsumeScopeAccessor) at construction time, so reading
            // the accessor from this callback would observe the disposed dispatcher scope.
            await FlushAggregatorAsync(descriptor, ambientScope: null, token).ConfigureAwait(false);
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

    private async Task FlushAggregatorAsync(AggregatorDescriptor descriptor, IServiceProvider? ambientScope, CancellationToken cancellationToken)
    {
        // Fast-fail if already disposed before we touch _flushLocks at all.
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        SemaphoreSlim flushLock;
        if (!_flushLocks.TryGetValue(descriptor.AggregatorName, out flushLock!))
        {
            // Re-check before allocating — DisposeAsync may have run between TryGetValue
            // and here. This minimises wasted work in the common post-dispose path.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var freshLock = new SemaphoreSlim(1, 1);
            flushLock = _flushLocks.GetOrAdd(descriptor.AggregatorName, freshLock);

            // If another thread won the GetOrAdd race, freshLock is surplus — dispose it.
            if (!ReferenceEquals(flushLock, freshLock))
            {
                freshLock.Dispose();
            }

            // Re-check _disposed: DisposeAsync's _flushLocks.Clear() may have run between
            // TryGetValue and GetOrAdd. Remove and dispose our (possibly just-inserted) lock
            // so it does not leak past the disposal foreach.
            if (Volatile.Read(ref _disposed) != 0)
            {
                _flushLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(descriptor.AggregatorName, flushLock));
                flushLock.Dispose();
                throw new ObjectDisposedException(nameof(AggregatorProcessor));
            }
        }

        await flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_timers.TryRemove(descriptor.AggregatorName, out var activeTimer))
            {
                await activeTimer.DisposeAsync().ConfigureAwait(false);
            }

            if (persistor == null)
            {
                return;
            }

            // Use the snapshot API so we can (a) remove only the specific records we dispatched,
            // leaving concurrently-inserted messages intact (closes the Get/RemoveAll race), and
            // (b) leave unresolved-type records in place instead of silently wiping them.
            var snapshot = await persistor.GetSnapshotAsync(descriptor.AggregatorName, cancellationToken).ConfigureAwait(false);
            if (snapshot.ResolvedMessages.Count == 0)
            {
                if (snapshot.UnresolvedCount > 0)
                {
                    logger.LogWarning(
                        "Aggregator {AggregatorName} has {UnresolvedCount} record(s) with unresolvable types; skipping dispatch until type is available",
                        descriptor.AggregatorName, snapshot.UnresolvedCount);
                }

                return;
            }

            // BuildTypedList expects IList<object>; copy the read-only snapshot into a fresh
            // mutable list. The snapshot itself stays immutable; this is the handler-facing
            // payload. IReadOnlyList<IHasCorrelationId> is not co-variant to IList<object>,
            // so an as-cast cannot avoid this allocation.
            List<object> resolvedList = [.. snapshot.ResolvedMessages];
            var typedList = descriptor.BuildTypedList(resolvedList);

            // The batch path passes its dispatcher-pushed scope through ambientScope. The timer
            // path passes null because the Timer captured the dispatcher's ExecutionContext at
            // construction, so reading the AsyncLocal here would return the now-disposed scope.
            IServiceScope? localScope = null;
            try
            {
                var resolverProvider = ambientScope ?? (localScope = scopeFactory.CreateScope()).ServiceProvider;

                var aggregator = resolverProvider.GetService(descriptor.AggregatorBaseType);
                if (aggregator == null)
                {
                    return;
                }

                // Execute first, then remove on success. On handler exception we propagate
                // without removing so the broker redelivers and the snapshot is re-flushable.
                // Cancellation also leaves the snapshot in place — by-design for retry on
                // next admission.
                await descriptor.InvokeExecuteAsync(aggregator, typedList, cancellationToken).ConfigureAwait(false);
                await persistor.RemoveSnapshotAsync(descriptor.AggregatorName, snapshot, cancellationToken).ConfigureAwait(false);

                if (snapshot.UnresolvedCount > 0)
                {
                    logger.LogWarning(
                        "Aggregator {AggregatorName} dispatched {Count} record(s); {UnresolvedCount} unresolved record(s) retained for a later flush",
                        descriptor.AggregatorName, snapshot.ResolvedMessages.Count, snapshot.UnresolvedCount);
                }
            }
            finally
            {
                localScope?.Dispose();
            }
        }
        finally
        {
            flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Signal all in-flight flushes to cancel.
        await _disposeCts.CancelAsync().ConfigureAwait(false);

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

        // Sequence the timer cleanup against ResetTimer via _resetTimerLock so a Timer
        // installed in the disposal window does not leak past the foreach.
        lock (_resetTimerLock)
        {
            foreach (var kvp in _timers)
            {
                kvp.Value.Dispose();
            }

            _timers.Clear();
        }

        foreach (var kvp in _flushLocks)
        {
            kvp.Value.Dispose();
        }

        _flushLocks.Clear();

        _disposeCts.Dispose();
    }
}
