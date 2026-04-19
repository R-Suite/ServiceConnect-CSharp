using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

/// <summary>
/// Hosted-service adapter that starts and stops bus consumption with the application host.
/// </summary>
public sealed class BusHostedService(IBus bus, IBusConfiguration config, ILogger<BusHostedService> logger) : IHostedService
{
    /// <summary>
    /// Starts the bus automatically when <see cref="IBusConfiguration.AutoStartConsuming"/> is enabled.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host startup.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!config.AutoStartConsuming)
        {
            logger.LogInformation("AutoStartConsuming is disabled.");
            return;
        }

        // Let exceptions propagate — the host should observe startup failures
        // rather than silently report success when consuming never started.
        await bus.StartConsumingAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Bus auto-started consuming.");
    }

    /// <summary>
    /// Stops bus consumption during host shutdown.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel host shutdown.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await bus.StopConsumingAsync(cancellationToken).ConfigureAwait(false);
    }
}
