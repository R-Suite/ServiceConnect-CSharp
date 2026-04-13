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

        try
        {
            await bus.StartConsumingAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Bus auto-started consuming.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Could not auto-start consuming. No consumer may be registered.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await bus.StopConsumingAsync(cancellationToken).ConfigureAwait(false);
    }
}
