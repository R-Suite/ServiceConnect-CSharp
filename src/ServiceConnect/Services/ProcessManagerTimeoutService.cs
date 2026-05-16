using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

/// <summary>
/// Hosted service that polls timeout storage and dispatches due process-manager timeout messages.
/// </summary>
internal sealed class ProcessManagerTimeoutService(
    IBusConfiguration config,
    Lazy<IBus> bus,
    ITimeoutStore? finder,
    ILogger<ProcessManagerTimeoutService> logger,
    TimeProvider? timeProvider = null) : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    // Safety margin — if the remaining lease is less than this, skip dispatch and let the
    // next poll reclaim the timeout rather than risk a duplicate send after the lease expires.
    private static readonly TimeSpan LeaseSafetyMargin = TimeSpan.FromSeconds(2);

    // Cancellation source independent of the IHostedService startup token. StartAsync's
    // cancellationToken parameter is for cancelling host startup, not for cancelling the
    // long-running poll loop afterwards. Mirror the standard BackgroundService pattern:
    // the startup CT is observed once; long-running work uses _stoppingCts which is
    // cancelled by StopAsync.
    private CancellationTokenSource? _stoppingCts;
    private Task? _pollingTask;
    private readonly Lazy<IBus> _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    private readonly ITimeoutStore? _finder = finder;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Starts timeout polling when process-manager timeouts are enabled and a timeout store is registered.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host startup.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!config.EnableProcessManagerTimeouts)
        {
            logger.LogDebug("Process manager timeouts are disabled.");
            return Task.CompletedTask;
        }

        if (_finder == null)
        {
            logger.LogWarning("EnableProcessManagerTimeouts is true but no ITimeoutStore registered.");
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var configured = config.ProcessManagerTimeoutPollInterval;
        var interval = configured <= TimeSpan.Zero ? DefaultPollInterval : configured;
        if (configured <= TimeSpan.Zero)
        {
            logger.LogWarning("ProcessManagerTimeoutPollInterval {Configured} is not positive; falling back to {Fallback}.", configured, DefaultPollInterval);
        }

        _stoppingCts = new CancellationTokenSource();
        _pollingTask = PollLoop(interval, _stoppingCts.Token);
        logger.LogInformation("Process manager timeout polling started.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops timeout polling and waits for the poll loop to finish.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host shutdown.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = Interlocked.Exchange(ref _stoppingCts, null);
        if (cts == null)
        {
            return; // never started or already stopped
        }

        try { await cts.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        var pollingCompleted = false;
        if (_pollingTask != null)
        {
#pragma warning disable VSTHRD003 // _pollingTask was started by StartAsync on this instance.
            try
            {
                await _pollingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                pollingCompleted = true;
            }
            catch (OperationCanceledException)
            {
                // Host grace token fired before the poll loop finished draining. The poll
                // loop is still running and may read cts.Token on its next iteration —
                // disposing the CTS now would surface as ObjectDisposedException out of the
                // PeriodicTimer's WaitForNextTickAsync and bubble through PollLoop's broad
                // catch as "poll loop terminated unexpectedly". Skip the Dispose; the
                // CancellationTokenSource holds no unmanaged state beyond the lazy WaitHandle
                // and the poll task carries its own reference so the source is GC-reclaimable
                // once the poll task completes.
            }
#pragma warning restore VSTHRD003
        }

        if (pollingCompleted)
        {
            cts.Dispose();
            _pollingTask = null;
        }
        else
        {
            // Attach a fault observer so any post-grace exception from the abandoned
            // polling task is observed rather than firing TaskScheduler.UnobservedTaskException.
            _ = _pollingTask?.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_finder == null)
        {
            return 0;
        }

        try
        {
            var batch = await _finder.GetTimeoutsBatchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (batch.DueTimeouts == null || batch.DueTimeouts.Count == 0)
            {
                return 0;
            }

            // sentCount drives the catch-up loop: it increments on every successful
            // SendAsync regardless of whether the subsequent Remove succeeded. Decoupling
            // it from the remove path means a transiently-failing store does not starve
            // the catch-up loop — sends still fire at full batch rate while the row stays
            // in the store for the next poll to re-attempt removal.
            //
            // Skips (margin gate and post-send lease-expiry) must NOT count — otherwise
            // the catch-up loop keeps re-polling against a backlog of skip-eligible rows
            // whose lease another worker has already re-claimed.
            var sentCount = 0;
            foreach (var timeout in batch.DueTimeouts)
            {
                try
                {
                    // Skip dispatch if the remaining lease is below the safety margin —
                    // another poller is about to reclaim this row and a duplicate send
                    // here would cause at-least-twice delivery.
                    if (timeout.LockExpiresAt.HasValue &&
                        timeout.LockExpiresAt.Value - _timeProvider.GetUtcNow() < LeaseSafetyMargin)
                    {
                        logger.LogDebug(
                            "Skipping timeout {TimeoutId}; lease expires at {Expires} (margin {Margin})",
                            timeout.Id, timeout.LockExpiresAt.Value, LeaseSafetyMargin);
                        continue;
                    }

                    logger.LogDebug("Dispatching timeout {TimeoutId} for PM {ProcessManagerId}",
                        timeout.Id, timeout.ProcessManagerId);

                    if (!string.IsNullOrEmpty(timeout.Destination))
                    {
                        var timeoutMessage = new TimeoutMessage(timeout.ProcessManagerId);
                        await _bus.Value.SendAsync(timeoutMessage, new SendOptions
                        {
                            EndPoint = timeout.Destination,
                            Headers = TimeoutHeaderPersistence.BuildOutgoingHeaders(timeout.Headers, logger)
                        }, cancellationToken).ConfigureAwait(false);

                        // Send succeeded: increment the catch-up signal. Empty-destination rows
                        // skip both the send and the catch-up bump — they are removed below but
                        // do not drive the loop forward.
                        sentCount++;
                    }

                    // Post-send lease check. SendAsync may have taken longer than the remaining
                    // lease; if so a peer poller may have already re-acquired and re-dispatched
                    // this row. Skip Remove and let the lease-expiry sweep reclaim the row on
                    // the next poll. The trade-off is a possible duplicate send (at-least-once
                    // timeout semantics, already documented), not a duplicate Remove racing a
                    // peer's lease reclaim.
                    if (timeout.LockExpiresAt.HasValue &&
                        timeout.LockExpiresAt.Value <= _timeProvider.GetUtcNow())
                    {
                        logger.LogWarning(
                            "Lease for timeout {TimeoutId} expired during SendAsync (expires at {Expires}); skipping Remove. " +
                            "Next poll will reclaim the row.",
                            timeout.Id, timeout.LockExpiresAt.Value);
                        continue;
                    }

                    // Pass the captured lease owner only when one is set — the store treats null
                    // as the unconditional id-only path and a non-null Guid as lease-checked.
                    Guid? lockOwner = timeout.LockedBy != Guid.Empty ? timeout.LockedBy : null;
                    // A remove failure is not a send failure: the message was already delivered.
                    // Swallow non-OCE remove errors and log a warning so the row stays in the
                    // store for the next poll to re-attempt removal (at-least-once semantics).
                    // Token propagation: StopAsync becomes bounded by the lifecycle token's
                    // deadline. A cancel-during-remove leaves the timeout "dispatched but not
                    // removed" — next poll redispatches, consistent with at-least-once semantics.
                    try
                    {
                        await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception removeEx)
                    {
                        logger.LogWarning(removeEx,
                            "Remove failed for timeout {TimeoutId} after successful send; row remains for next poll.",
                            timeout.Id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error dispatching timeout {TimeoutId}", timeout.Id);

                    try
                    {
                        Guid? lockOwner = timeout.LockedBy != Guid.Empty ? timeout.LockedBy : null;
                        await _finder.ReleaseDispatchedTimeoutAsync(timeout.Id, lockOwner, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception releaseEx) when (releaseEx is not OperationCanceledException)
                    {
                        logger.LogError(releaseEx, "Error releasing timeout {TimeoutId} after dispatch failure", timeout.Id);
                    }
                }
            }

            return sentCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shutdown CT cancelled us; clean exit. This branch handles the legitimate
            // shutdown OCE; the next branch handles any other OCE (e.g., a Bus.SendAsync's
            // internal cancellation that doesn't share our token) so it doesn't escape silently.
            return 0;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Unexpected OperationCanceledException polling for process manager timeouts (not the shutdown token).");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling for process manager timeouts");
            return 0;
        }
    }

    // Per-tick catch-up cap. After a multi-hour outage the timeout store can hold a large
    // backlog; without a catch-up loop, drain rate is `BatchSize / PollInterval` (e.g.
    // 500/30s ≈ 16/s for default config — a 1 M backlog takes ~17 h to clear). When a poll
    // returns a non-empty batch we re-poll immediately up to this cap before waiting for
    // the next tick, so the steady-state continues to back off while a backlog drains
    // quickly. 32 iterations × default 500 batch = 16k timeouts processed per outer tick
    // before yielding to the next scheduled poll.
    private const int MaxCatchUpIterationsPerTick = 32;

    private async Task PollLoop(TimeSpan interval, CancellationToken cancellationToken)
    {
        // Use the TimeProvider-aware PeriodicTimer overload so tests with FakeTimeProvider
        // can drive the loop. The parameterless `new PeriodicTimer(interval)` ignores the
        // injected _timeProvider — only TimeProvider.System would fire the timer, leaving
        // time-controlled tests unable to advance the loop.
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Catch-up loop: while the previous batch was non-empty, immediately
                // re-poll without waiting for the next tick. The cap prevents a continuous
                // flood of past-due timeouts from starving the rest of the host's tasks.
                for (var i = 0; i < MaxCatchUpIterationsPerTick; i++)
                {
                    var dispatched = await PollOnceAsync(cancellationToken).ConfigureAwait(false);
                    if (dispatched == 0)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — clean exit.
        }
        catch (Exception ex)
        {
            // PollOnceAsync catches its own exceptions, so reaching here implies the
            // timer itself faulted. Log and exit; the host's StopAsync observes the
            // task completion.
            logger.LogError(ex, "ProcessManagerTimeoutService poll loop terminated unexpectedly.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Mirror StopAsync: Interlocked.Exchange claims exclusive ownership of _stoppingCts so
        // a racing StopAsync+DisposeAsync pair can't both call Dispose on the same CTS.
        var cts = Interlocked.Exchange(ref _stoppingCts, null);
        if (cts != null)
        {
            try { await cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        // Snapshot the polling task to a local so a racing StopAsync that nulls the field
        // after our check cannot trip a null-deref on the abandoned-task observer below.
        var pollingTask = _pollingTask;
        var pollingCompleted = false;
        if (pollingTask != null)
        {
#pragma warning disable VSTHRD003 // _pollingTask was started by StartAsync on this instance.
            // Bound the wait so a non-cooperative ITimeoutStore (sync-over-async wedge,
            // hung network call, etc.) cannot wedge DI shutdown. On timeout the polling
            // task is left to GC; any in-flight Send/Remove will complete or be torn
            // down by the cancelled CTS captured above. We attach an unobserved-fault
            // observer in that case so an eventual fault on the abandoned task does not
            // surface as a `TaskScheduler.UnobservedTaskException` event at finalization.
            try
            {
                await pollingTask.WaitAsync(config.DisposeTimeout).ConfigureAwait(false);
                pollingCompleted = true;
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                logger.LogWarning(
                    "Polling task did not complete within {Timeout}; abandoning the await and continuing dispose.",
                    config.DisposeTimeout);
                _ = pollingTask.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
#pragma warning restore VSTHRD003
        }
        // Mirror StopAsync's timeout-path semantics: only dispose the CTS when the polling
        // task actually completed. When the WaitAsync timed out / was cancelled, the task
        // is still running and may read cts.Token on its next loop iteration — disposing
        // here would surface as ObjectDisposedException inside PollLoop's PeriodicTimer and
        // log as "poll loop terminated unexpectedly". The fault observer above ensures the
        // abandoned task's eventual fault is observed; the CTS is GC-reclaimable when the
        // task finally completes.
        if (pollingCompleted)
        {
            cts?.Dispose();
        }
    }
}
