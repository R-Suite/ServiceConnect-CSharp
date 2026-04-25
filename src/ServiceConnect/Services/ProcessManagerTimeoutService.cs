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

    private CancellationTokenSource? _cts;
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

        var configured = config.ProcessManagerTimeoutPollInterval;
        var interval = configured <= TimeSpan.Zero ? DefaultPollInterval : configured;
        if (configured <= TimeSpan.Zero)
            logger.LogWarning("ProcessManagerTimeoutPollInterval {Configured} is not positive; falling back to {Fallback}.", configured, DefaultPollInterval);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = PollLoop(interval, _cts.Token);
        logger.LogInformation("Process manager timeout polling started.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops timeout polling and waits for the poll loop to finish.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host shutdown.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            cts.Cancel();
            if (_pollingTask != null)
            {
                try { await _pollingTask; }
                catch (OperationCanceledException) { }
            }

            cts.Dispose();
        }

        _pollingTask = null;
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_finder == null) return;

        try
        {
            var batch = await _finder.GetTimeoutsBatchAsync(cancellationToken).ConfigureAwait(false);
            if (batch.DueTimeouts == null || batch.DueTimeouts.Count == 0) return;

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

                    // Pass the captured lease owner only when one is set — the store treats null
                    // as the unconditional id-only path and a non-null Guid as lease-checked.
                    Guid? lockOwner = timeout.LockedBy != Guid.Empty ? timeout.LockedBy : null;
                    await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, lockOwner, CancellationToken.None).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error polling for process manager timeouts");
        }
    }

    private async Task PollLoop(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Mirror StopAsync: Interlocked.Exchange claims exclusive ownership of _cts so a racing
        // StopAsync+DisposeAsync pair can't both call Dispose on the same CTS.
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
    }
}
