using System.Runtime.CompilerServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Unhealthy when the broker has cancelled the consumer; otherwise Healthy when
/// <see cref="IConsumer.IsConnected"/> is <see langword="true"/>; otherwise grace window
/// applies (Healthy if within the window since the last observed-Healthy probe, Unhealthy
/// once the window expires or if the consumer has never been observed Healthy).
/// Recovery grace ensures a momentary disconnect (auto-recovery, network blip) does not
/// crash-loop pods wired on liveness probes.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// First-probe behaviour: a check that has never observed Healthy returns Unhealthy regardless
/// of the grace window. The grace is meant for readiness probes.
/// </remarks>
public sealed class ConsumerConnectionHealthCheck : IHealthCheck
{
    // Per-IConsumer recovery state. Mirrors BusConsumingHealthCheck — see the comment
    // there for the per-probe re-allocation rationale that motivates the external
    // ConditionalWeakTable.
    private static readonly ConditionalWeakTable<IConsumer, HealthCheckRecoveryState> RecoveryStateByConsumer = [];

    private readonly IConsumer _consumer;
    private readonly TimeSpan _recoveryGraceWindow;
    private readonly TimeProvider _timeProvider;
    private readonly HealthCheckRecoveryState _recoveryState;

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
        _recoveryState = RecoveryStateByConsumer.GetValue(consumer, static _ => new HealthCheckRecoveryState());
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Broker-cancelled is a permanent failure — bypass grace and the IsConnected
        // check. basic.cancel (queue deleted, policy expired, mirror promoted) tears down
        // the consumer registration but leaves the AMQP TCP connection up, so IsConnected
        // can remain true while deliveries have stopped. Checking broker-cancel first
        // ensures readiness probes remove the pod from the load balancer immediately.
        if (_consumer.IsCancelledByBroker)
        {
            var brokerFailureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
            return Task.FromResult(new HealthCheckResult(brokerFailureStatus,
                "Consumer connection is closed (broker cancelled the consumer)."));
        }

        if (_consumer.IsConnected)
        {
            Volatile.Write(ref _recoveryState.LastHealthyTicks, _timeProvider.GetUtcNow().UtcTicks);
            return Task.FromResult(HealthCheckResult.Healthy("Consumer connection is open."));
        }

        // Intentional shutdown is a permanent failure — bypass grace. The grace window
        // is meant to absorb transient disconnects where reconnect can recover; once the
        // consumer has been stopped or disposed there is no recovery to wait for.
        if (_consumer.IsStopped)
        {
            var stoppedFailureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
            return Task.FromResult(new HealthCheckResult(stoppedFailureStatus,
                "Consumer connection is closed (stopped or disposed)."));
        }

        var lastHealthy = Volatile.Read(ref _recoveryState.LastHealthyTicks);
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
