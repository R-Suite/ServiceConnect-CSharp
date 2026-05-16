using System.Runtime.CompilerServices;
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
    // Per-IBus recovery state. The bus is a DI singleton (stable identity across
    // probes), so a ConditionalWeakTable keyed on it bridges the per-probe re-alloc
    // that PerProviderCache + HealthCheckService impose: each probe runs against a
    // fresh IServiceScope.ServiceProvider, so the cache produces a new check instance
    // per probe; without an external state table, the instance-scoped LastHealthyTicks
    // resets to 0 every probe and the grace window never triggers. CWT keys by
    // reference identity and tracks GC reachability, so a rebuilt SP-with-new-IBus
    // gets a fresh state and the prior state becomes GC-eligible.
    private static readonly ConditionalWeakTable<IBus, HealthCheckRecoveryState> RecoveryStateByBus = [];

    private readonly IBus _bus;
    private readonly IConsumer? _consumer;
    private readonly TimeSpan _recoveryGraceWindow;
    private readonly TimeProvider _timeProvider;
    private readonly HealthCheckRecoveryState _recoveryState;

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
        _recoveryState = RecoveryStateByBus.GetValue(bus, static _ => new HealthCheckRecoveryState());
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
            // Interlocked.Exchange (not Volatile.Write) because long writes are not atomic
            // on 32-bit runtimes (.NET Framework x86, some embedded ARM32). A torn 8-byte
            // write could materialise a half-written ticks value outside DateTimeOffset's
            // legal range, which the reader's ctor below would AOORE on. The 64-bit fence
            // costs ~1ns per probe and removes the 32-bit hazard entirely.
            Interlocked.Exchange(ref _recoveryState.LastHealthyTicks, _timeProvider.GetUtcNow().UtcTicks);
            return Task.FromResult(HealthCheckResult.Healthy("Bus is consuming."));
        }

        // Broker-cancelled is a permanent failure — bypass grace. Prefer the explicit
        // IConsumer signal when supplied; otherwise consult IBus.IsCancelledByBroker so
        // the parameterless-ctor path also short-circuits on broker basic.cancel events
        // (queue deleted, policy expired, mirror promoted) without waiting out the grace
        // window. Both signals reduce to the same underlying IConsumer.IsCancelledByBroker.
        if (_consumer is { IsCancelledByBroker: true } || _bus.IsCancelledByBroker)
        {
            var brokerFailureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
            return Task.FromResult(new HealthCheckResult(brokerFailureStatus,
                "Bus is not consuming (broker cancelled the consumer)."));
        }

        // Intentional shutdown is a permanent failure — bypass grace. The grace window is
        // meant to absorb transient disconnects where reconnect can recover; once the bus
        // has been stopped or disposed there is no recovery to wait for, so a probe that
        // reports Healthy here would mask a permanently-dead bus for the grace duration.
        if (_bus.IsStopped)
        {
            var stoppedFailureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;
            return Task.FromResult(new HealthCheckResult(stoppedFailureStatus,
                "Bus is not consuming (stopped or disposed)."));
        }

        // Recovery grace: if we've observed Healthy at some point AND we're within the
        // grace window, return Healthy with a note. Per-bus state survives the per-probe
        // re-alloc so a momentary disconnect does not flip Unhealthy and crash-loop the pod.
        // Interlocked.Read pairs with Interlocked.Exchange above — 8-byte atomic on every
        // architecture, including 32-bit. Volatile.Read on a long does NOT guarantee atomic
        // read on 32-bit.
        var lastHealthy = Interlocked.Read(ref _recoveryState.LastHealthyTicks);
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
