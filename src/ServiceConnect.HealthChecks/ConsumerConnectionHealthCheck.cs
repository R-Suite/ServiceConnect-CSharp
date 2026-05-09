using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IConsumer.IsConnected"/> is <see langword="true"/>, OR when
/// the consumer has been observed Healthy within the configured recovery-grace window AND the
/// consumer has not been broker-cancelled. Recovery grace ensures a momentary disconnect
/// (auto-recovery, network blip) does not crash-loop pods wired on liveness probes.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// First-probe behaviour: a check that has never observed Healthy returns Unhealthy regardless
/// of the grace window. The grace is meant for readiness probes.
/// </remarks>
public sealed class ConsumerConnectionHealthCheck : IHealthCheck
{
    private readonly IConsumer _consumer;
    private readonly TimeSpan _recoveryGraceWindow;
    private readonly TimeProvider _timeProvider;
    private long _lastHealthyTicks;  // 0 == never-observed-Healthy.

    /// <summary>
    /// Default 30-second recovery grace; system <see cref="TimeProvider"/>.
    /// </summary>
    public ConsumerConnectionHealthCheck(IConsumer consumer)
        : this(consumer, recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: TimeProvider.System)
    {
    }

    /// <summary>
    /// Configurable recovery-grace window and injectable <see cref="TimeProvider"/> for tests.
    /// </summary>
    /// <param name="consumer">Consumer to observe via <see cref="IConsumer.IsConnected"/>.</param>
    /// <param name="recoveryGraceWindow">
    /// Window after the most recent Healthy observation during which a transient
    /// <see cref="IConsumer.IsConnected"/>=false observation continues to report Healthy.
    /// Pass <see cref="TimeSpan.Zero"/> to disable grace.
    /// </param>
    /// <param name="timeProvider"><see cref="TimeProvider"/> used for grace-window measurement; injectable for tests.</param>
    public ConsumerConnectionHealthCheck(
        IConsumer consumer,
        TimeSpan recoveryGraceWindow,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (recoveryGraceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryGraceWindow),
                "Recovery grace window must be non-negative; pass TimeSpan.Zero to disable grace.");
        }
        _consumer = consumer;
        _recoveryGraceWindow = recoveryGraceWindow;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_consumer.IsConnected)
        {
            Volatile.Write(ref _lastHealthyTicks, _timeProvider.GetUtcNow().UtcTicks);
            return Task.FromResult(HealthCheckResult.Healthy("Consumer connection is open."));
        }

        if (_consumer.IsCancelledByBroker)
        {
            var brokerFailureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
            return Task.FromResult(new HealthCheckResult(brokerFailureStatus,
                "Consumer connection is closed (broker cancelled the consumer)."));
        }

        var lastHealthy = Volatile.Read(ref _lastHealthyTicks);
        if (lastHealthy != 0 && _recoveryGraceWindow > TimeSpan.Zero)
        {
            var age = _timeProvider.GetUtcNow() - new DateTimeOffset(lastHealthy, TimeSpan.Zero);
            if (age < _recoveryGraceWindow)
            {
                return Task.FromResult(HealthCheckResult.Healthy(
                    $"Consumer connection is closed, but within recovery grace ({age:c} < {_recoveryGraceWindow:c})."));
            }
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Consumer connection is closed."));
    }
}
