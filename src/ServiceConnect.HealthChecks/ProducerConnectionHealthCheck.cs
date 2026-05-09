using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IProducer.IsHealthy"/> is <see langword="true"/>,
/// OR when the producer has not yet attempted any connection (lazy-connect state).
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// The producer connects lazily on the first publish/send call. Pre-v8 this check
/// returned Unhealthy in that pre-publish window, which crash-looped readiness probes.
/// v8: NotYetAttempted is treated as Healthy; once a publish is attempted and fails,
/// transitions to <see cref="HealthStatus.Unhealthy"/>.
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

        // Single-snapshot read: pre-fix the check read IsHealthy and HasAttemptedConnection
        // separately. A publish-success transition between the reads (T2 sets both) could
        // surface to a probe (T1) as IsHealthy=false (stale) + HasAttemptedConnection=true
        // (fresh) — a false-negative Unhealthy. GetHealthSnapshot reads the pair atomically
        // (or, for third-party producers using the default impl, at least produces a typed
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
