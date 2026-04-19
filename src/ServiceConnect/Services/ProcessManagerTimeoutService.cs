using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Interfaces.Options;

namespace ServiceConnect.Services;

public sealed class ProcessManagerTimeoutService(
    IBusConfiguration config,
    Lazy<IBus> bus,
    ITimeoutStore? finder,
    ILogger<ProcessManagerTimeoutService> logger) : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private readonly Lazy<IBus> _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    private readonly ITimeoutStore? _finder = finder;
    private readonly ILeaseAwareTimeoutStore? _leaseAwareFinder = finder as ILeaseAwareTimeoutStore;

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
                    logger.LogDebug("Dispatching timeout {TimeoutId} for PM {ProcessManagerId}",
                        timeout.Id, timeout.ProcessManagerId);

                    if (!string.IsNullOrEmpty(timeout.Destination))
                    {
                        var timeoutMessage = new TimeoutMessage(timeout.ProcessManagerId);
                        await _bus.Value.SendAsync(timeoutMessage, new SendOptions
                        {
                            EndPoint = timeout.Destination,
                            Headers = TimeoutHeaderPersistence.BuildOutgoingHeaders(timeout.Headers)
                        }, cancellationToken).ConfigureAwait(false);
                    }

                    if (_leaseAwareFinder != null && timeout.LockedBy != Guid.Empty)
                        await _leaseAwareFinder.RemoveDispatchedTimeoutAsync(timeout.Id, timeout.LockedBy, CancellationToken.None).ConfigureAwait(false);
                    else
                        await _finder.RemoveDispatchedTimeoutAsync(timeout.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error dispatching timeout {TimeoutId}", timeout.Id);

                    try
                    {
                        if (_leaseAwareFinder != null && timeout.LockedBy != Guid.Empty)
                            await _leaseAwareFinder.ReleaseDispatchedTimeoutAsync(timeout.Id, timeout.LockedBy, cancellationToken).ConfigureAwait(false);
                        else
                            await _finder.ReleaseDispatchedTimeoutAsync(timeout.Id, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }
}
