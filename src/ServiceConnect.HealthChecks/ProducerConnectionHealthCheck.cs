using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IProducer.IsHealthy"/> is <see langword="true"/>,
/// OR when the producer has not yet attempted any connection (lazy-connect state).
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// The producer connects lazily on the first publish/send call. The "never attempted"
/// state is treated as Healthy so a readiness probe that runs before the first publish
/// does not crash-loop the pod; the check transitions to <see cref="HealthStatus.Unhealthy"/>
/// only once a publish has been attempted and failed.
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

        // Single-snapshot read: reading IsHealthy and HasAttemptedConnection separately
        // would let a publish-success transition between the reads (T2 sets both) surface
        // to a probe (T1) as IsHealthy=false (stale) + HasAttemptedConnection=true (fresh)
        // — a false-negative Unhealthy. GetHealthSnapshot reads the pair atomically (or,
        // for third-party producers using the default impl, at least produces a typed
        // result that future maintainers can spot as the pair-read site).
        var snapshot = _producer.GetHealthSnapshot();

        if (snapshot.IsHealthy)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Producer connection is open."));
        }

        if (!snapshot.HasAttemptedConnection)
        {
            // Producer connects lazily on the first publish/send. Until that happens,
            // "no connection" is the expected state, not a fault — readiness probes
            // shouldn't crash-loop pods that haven't published yet.
            return Task.FromResult(HealthCheckResult.Healthy(
                "Producer has not yet attempted connection (lazy)."));
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Producer connection is closed."));
    }
}
