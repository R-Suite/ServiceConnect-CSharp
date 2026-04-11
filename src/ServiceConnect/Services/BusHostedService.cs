using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Services;

public class BusHostedService : IHostedService
{
    private readonly IBus _bus;
    private readonly IBusConfiguration _config;
    private readonly ILogger<BusHostedService> _logger;

    public BusHostedService(IBus bus, IBusConfiguration config, ILogger<BusHostedService> logger)
    {
        _bus = bus;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.AutoStartConsuming)
        {
            _logger.LogInformation("AutoStartConsuming is disabled.");
            return;
        }

        try
        {
            await _bus.StartConsumingAsync();
            _logger.LogInformation("Bus auto-started consuming.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not auto-start consuming. No consumer may be registered.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _bus.StopConsuming();
        return Task.CompletedTask;
    }
}
