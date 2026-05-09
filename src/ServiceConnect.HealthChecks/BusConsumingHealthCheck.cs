using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Reports Healthy when <see cref="IBus.IsConsuming"/> is <see langword="true"/>, OR when the
/// bus has been observed Healthy within the configured recovery-grace window AND the underlying
/// consumer has not been broker-cancelled. Recovery grace ensures a momentary broker disconnect
/// (auto-recovery, network blip, broker bounce) does not crash-loop pods wired on liveness probes.
/// O(1), allocation-light, side-effect-free — does not perform broker I/O.
/// </summary>
/// <remarks>
/// <para>
/// The grace window is meant for readiness probes; liveness probes that want immediate flip on
/// disconnect should use a zero-grace window (or rely on the broker-cancelled short-circuit which
/// always bypasses grace).
/// </para>
/// <para>
/// First-probe behaviour: a check that has never observed Healthy returns Unhealthy regardless
/// of the grace window. There is no equivalent to <see cref="IProducer.HasAttemptedConnection"/>
/// for a consumer; a never-Healthy consumer is genuinely unhealthy, not lazy.
/// </para>
/// </remarks>
public sealed class BusConsumingHealthCheck : IHealthCheck
{
    private readonly IBus _bus;
    private readonly IConsumer? _consumer;
    private readonly TimeSpan _recoveryGraceWindow;
    private readonly TimeProvider _timeProvider;
    private long _lastHealthyTicks;  // 0 == never-observed-Healthy.

    /// <summary>
    /// Default 30-second recovery grace; system <see cref="TimeProvider"/>; no consumer
    /// supplied (no broker-cancelled short-circuit).
    /// </summary>
    public BusConsumingHealthCheck(IBus bus)
        : this(bus, consumer: null, recoveryGraceWindow: TimeSpan.FromSeconds(30), timeProvider: TimeProvider.System)
    {
    }

    /// <summary>
    /// Configurable recovery-grace window and optional consumer for broker-cancelled short-circuit.
    /// </summary>
    /// <param name="bus">Bus to observe via <see cref="IBus.IsConsuming"/>.</param>
    /// <param name="consumer">
    /// Optional consumer; when supplied, <see cref="IConsumer.IsCancelledByBroker"/> short-circuits
    /// the grace path so a permanent broker-cancellation flips Unhealthy immediately.
    /// </param>
    /// <param name="recoveryGraceWindow">
    /// Window after the most recent Healthy observation during which a transient
    /// <see cref="IBus.IsConsuming"/>=false observation continues to report Healthy.
    /// Pass <see cref="TimeSpan.Zero"/> to disable grace.
    /// </param>
    /// <param name="timeProvider"><see cref="TimeProvider"/> used for grace-window measurement; injectable for tests.</param>
    public BusConsumingHealthCheck(
        IBus bus,
        IConsumer? consumer,
        TimeSpan recoveryGraceWindow,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (recoveryGraceWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryGraceWindow),
                "Recovery grace window must be non-negative; pass TimeSpan.Zero to disable grace.");
        }
        _bus = bus;
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

        if (_bus.IsConsuming)
        {
            // Stamp the last-Healthy timestamp on every Healthy observation so the grace
            // window measures from "most recent Healthy" rather than "first ever Healthy".
            Volatile.Write(ref _lastHealthyTicks, _timeProvider.GetUtcNow().UtcTicks);
            return Task.FromResult(HealthCheckResult.Healthy("Bus is consuming."));
        }

        // Broker-cancelled is a permanent failure — bypass grace.
        if (_consumer is { IsCancelledByBroker: true })
        {
            var brokerFailureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
            return Task.FromResult(new HealthCheckResult(brokerFailureStatus,
                "Bus is not consuming (broker cancelled the consumer)."));
        }

        // Recovery grace: if we've observed Healthy at some point AND we're within the
        // grace window, return Healthy with a note. Pre-fix any momentary disconnect
        // would flip Unhealthy on the next probe and crash-loop the pod.
        var lastHealthy = Volatile.Read(ref _lastHealthyTicks);
        if (lastHealthy != 0 && _recoveryGraceWindow > TimeSpan.Zero)
        {
            var age = _timeProvider.GetUtcNow() - new DateTimeOffset(lastHealthy, TimeSpan.Zero);
            if (age < _recoveryGraceWindow)
            {
                return Task.FromResult(HealthCheckResult.Healthy(
                    $"Bus is not consuming, but within recovery grace ({age:c} < {_recoveryGraceWindow:c})."));
            }
        }

        var failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
        return Task.FromResult(new HealthCheckResult(failureStatus, "Bus is not consuming."));
    }
}
