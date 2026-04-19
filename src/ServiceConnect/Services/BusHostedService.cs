using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public sealed class BusHostedService(IBus bus, IBusConfiguration config, ILogger<BusHostedService> logger) : IHostedService
{
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await bus.StopConsumingAsync(cancellationToken).ConfigureAwait(false);
    }
}
