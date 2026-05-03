using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IConsumer.IsConnected"/> is <see langword="true"/>.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
public sealed class ConsumerConnectionHealthCheck : IHealthCheck
{
    private readonly IConsumer _consumer;

    /// <summary>
    /// Creates a check that observes the supplied <see cref="IConsumer"/>.
    /// </summary>
    public ConsumerConnectionHealthCheck(IConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _consumer = consumer;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_consumer.IsConnected)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Consumer connection is open."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Consumer connection is closed."));
    }
}
