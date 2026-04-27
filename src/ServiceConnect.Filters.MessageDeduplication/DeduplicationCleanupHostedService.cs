using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceConnect.Filters.MessageDeduplication.Persistors;

namespace ServiceConnect.Filters.MessageDeduplication;

/// <summary>
/// Periodically removes expired message ids from the deduplication persistor.
/// Honors <see cref="DeduplicationFilterSettings.DisableMsgExpiry"/>.
/// </summary>
public sealed class DeduplicationCleanupHostedService : BackgroundService
{
    private readonly IMessageDeduplicationPersistor _persistor;
    private readonly bool _disableMsgExpiry;
    private readonly TimeSpan _interval;
    private readonly ILogger<DeduplicationCleanupHostedService> _logger;

    public DeduplicationCleanupHostedService(
        IMessageDeduplicationPersistor persistor,
        IOptions<DeduplicationFilterSettings> options,
        ILogger<DeduplicationCleanupHostedService> logger)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _disableMsgExpiry = options.Value.DisableMsgExpiry;
        _interval = TimeSpan.FromMinutes(options.Value.MsgCleanupIntervalMinutes);
    }

    private DeduplicationCleanupHostedService(
        IMessageDeduplicationPersistor persistor,
        bool disableMsgExpiry,
        TimeSpan interval,
        ILogger<DeduplicationCleanupHostedService> logger)
    {
        _persistor = persistor ?? throw new ArgumentNullException(nameof(persistor));
        _disableMsgExpiry = disableMsgExpiry;
        _interval = interval;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Internal test seam — allows sub-minute intervals for testing the loop.
    internal static DeduplicationCleanupHostedService CreateForTesting(
        IMessageDeduplicationPersistor persistor,
        bool disableMsgExpiry,
        TimeSpan interval,
        ILogger<DeduplicationCleanupHostedService> logger)
    {
        return new DeduplicationCleanupHostedService(persistor, disableMsgExpiry, interval, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_disableMsgExpiry)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                await _persistor.RemoveExpiredMessagesAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == stoppingToken)
            {
                return; // graceful shutdown — cancellation came from our stoppingToken
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during dedup cleanup; will retry on next interval");
            }
        }
    }
}
