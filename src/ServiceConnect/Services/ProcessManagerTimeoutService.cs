using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public class ProcessManagerTimeoutService : IHostedService, IDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private readonly IBusConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProcessManagerTimeoutService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private IProcessManagerFinder? _finder;

    public ProcessManagerTimeoutService(
        IBusConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<ProcessManagerTimeoutService> logger)
    {
        _config = config;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.EnableProcessManagerTimeouts)
        {
            _logger.LogDebug("Process manager timeouts are disabled.");
            return Task.CompletedTask;
        }

        _finder = _serviceProvider.GetService<IProcessManagerFinder>();
        if (_finder == null)
        {
            _logger.LogWarning("EnableProcessManagerTimeouts is true but no IProcessManagerFinder registered.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = PollLoop(_cts.Token);
        _logger.LogInformation("Process manager timeout polling started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_pollingTask != null)
            {
                try { await _pollingTask; }
                catch (OperationCanceledException) { }
            }
        }
    }

    internal async Task PollOnceAsync()
    {
        _finder ??= _serviceProvider.GetService<IProcessManagerFinder>();
        if (_finder == null) return;

        try
        {
            var batch = _finder.GetTimeoutsBatch();
            if (batch.DueTimeouts == null || batch.DueTimeouts.Count == 0) return;

            foreach (var timeout in batch.DueTimeouts)
            {
                try
                {
                    _logger.LogDebug("Dispatching timeout {TimeoutId} for PM {ProcessManagerId}",
                        timeout.Id, timeout.ProcessManagerId);
                    _finder.RemoveDispatchedTimeout(timeout.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error dispatching timeout {TimeoutId}", timeout.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling for process manager timeouts");
        }
    }

    private async Task PollLoop(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DefaultPollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
                await PollOnceAsync();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
