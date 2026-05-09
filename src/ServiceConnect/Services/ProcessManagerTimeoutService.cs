using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

/// <summary>
/// Hosted service that polls timeout storage and dispatches due process-manager timeout messages.
/// </summary>
public sealed class ProcessManagerTimeoutService(
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

        if (_pollingTask != null)
        {
#pragma warning disable VSTHRD003 // _pollingTask was started by StartAsync on this instance.
            try { await _pollingTask.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
#pragma warning restore VSTHRD003
        }

        cts.Dispose();
        _pollingTask = null;
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_finder == null)
        {
            return;
        }

        try
        {
            var batch = await _finder.GetTimeoutsBatchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (batch.DueTimeouts == null || batch.DueTimeouts.Count == 0)
            {
                return;
            }

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
                    // See learn/operations/cancellation: token propagation rule. StopAsync becomes
                    // bounded by the lifecycle token's deadline. A cancel-during-remove leaves the
                    // timeout "dispatched but not removed" — next poll redispatches, consistent with
                    // the existing at-least-once timeout semantics.
                    await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, cancellationToken).ConfigureAwait(false);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shutdown CT cancelled us; clean exit. Pre-fix the catch was
            // `when (ex is not OperationCanceledException)`, which let any non-loop OCE
            // escape silently (e.g., a Bus.SendAsync's internal cancellation that doesn't
            // share our token). Distinguish: this branch handles the legitimate shutdown
            // OCE; the next branch handles any other OCE.
            return;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Unexpected OperationCanceledException polling for process manager timeouts (not the shutdown token).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error polling for process manager timeouts");
        }
    }

    private async Task PollLoop(TimeSpan interval, CancellationToken cancellationToken)
    {
        // Use the TimeProvider-aware PeriodicTimer overload so tests with FakeTimeProvider
        // can drive the loop. Pre-fix `new PeriodicTimer(interval)` ignored the injected
        // _timeProvider — only TimeProvider.System could fire the timer.
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — clean exit.
        }
        catch (Exception ex)
        {
            // PollOnceAsync now catches its own exceptions (M22 fix) so reaching here
            // implies the timer itself faulted. Log and exit; the host's StopAsync
            // observes the task completion.
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
        if (_pollingTask != null)
        {
#pragma warning disable VSTHRD003 // _pollingTask was started by StartAsync on this instance.
            try { await _pollingTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
#pragma warning restore VSTHRD003
        }
        cts?.Dispose();
    }
}
