using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IBus.IsConsuming"/> is <see langword="true"/>.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
public sealed class BusConsumingHealthCheck : IHealthCheck
{
    private readonly IBus _bus;

    /// <summary>
    /// Creates a check that observes the supplied <see cref="IBus"/>.
    /// </summary>
    public BusConsumingHealthCheck(IBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_bus.IsConsuming)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Bus is consuming."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Bus is not consuming."));
    }
}
