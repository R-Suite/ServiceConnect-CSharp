using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IProducer.IsHealthy"/> is <see langword="true"/>.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// The producer connects lazily on the first publish/send call, so this check
/// reports Unhealthy until the host has published at least once. Hosts that do
/// not publish at startup should not register this check on a readiness tag.
/// </remarks>
public sealed class ProducerConnectionHealthCheck : IHealthCheck
{
    private readonly IProducer _producer;

    /// <summary>
    /// Creates a check that observes the supplied <see cref="IProducer"/>.
    /// </summary>
    public ProducerConnectionHealthCheck(IProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        _producer = producer;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_producer.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Producer connection is open."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Producer connection is closed."));
    }
}
