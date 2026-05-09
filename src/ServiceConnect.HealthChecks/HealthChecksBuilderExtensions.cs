using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceConnect.Interfaces;

namespace ServiceConnect.HealthChecks;

/// <summary>
/// Extension methods on <see cref="IHealthChecksBuilder"/> for registering
/// ServiceConnect health checks. Each method registers exactly one check.
/// Pick the methods that match what your host actually does — a publish-only
/// host should not register the consumer check, a consume-only host should
/// not register the producer check.
/// </summary>
public static class HealthChecksBuilderExtensions
{
    // ── Bus ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a health check that reports Healthy when the bus is consuming
    /// (<see cref="IBus.IsConsuming"/>). Resolves <see cref="IBus"/> from the
    /// DI container via <see cref="ServiceProviderServiceExtensions.GetRequiredService{T}"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectBus(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-bus",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        => builder.AddServiceConnectBus(name,
            sp => sp.GetRequiredService<IBus>(),
            failureStatus, tags, timeout);

    /// <summary>
    /// Registers a bus-consuming health check resolving the bus via a keyed-services key.
    /// Convenience wrapper over the factory overload for hosts using
    /// <see cref="ServiceProviderKeyedServiceExtensions"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectBus(
        this IHealthChecksBuilder builder,
        string name,
        object serviceKey,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        => builder.AddServiceConnectBus(name,
            sp => sp.GetRequiredKeyedService<IBus>(serviceKey),
            failureStatus, tags, timeout);

    /// <summary>
    /// Registers a bus-consuming health check resolving the bus via a factory function.
    /// Use this for non-DI-resolved buses or for custom keyed-services patterns.
    /// The factory runs on every probe; the underlying <see cref="IBus"/> singleton
    /// is cached by the supplied <see cref="IServiceProvider"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectBus(
        this IHealthChecksBuilder builder,
        string name,
        Func<IServiceProvider, IBus> busFactory,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(busFactory);
        // Cache the wrapper per IServiceProvider via ConditionalWeakTable. The earlier
        // pre-fix used LazyInitializer.EnsureInitialized which captured a closure-cached
        // instance that outlived the resolving IServiceProvider — rebuilt providers
        // probed an OLD disposed bus from a stale check. The per-SP cache here both
        // (a) honours M6's rebuild contract (rebuilt SP becomes GC-eligible and gets a
        // fresh check on next probe) AND (b) preserves M4's recovery-grace state
        // (instance-scoped _lastHealthyTicks is stable across probes against the same SP).
        var cache = new PerProviderCache<BusConsumingHealthCheck>(
            sp => new BusConsumingHealthCheck(busFactory(sp)));
        return builder.Add(new HealthCheckRegistration(
            name,
            cache.Resolve,
            failureStatus,
            tags,
            timeout));
    }

    /// <summary>
    /// Registers a bus-consuming health check with a configurable recovery-grace window and
    /// optional <see cref="TimeProvider"/>. Use this overload when the host needs deterministic
    /// time control (tests with FakeTimeProvider) or a non-default grace window. An optional
    /// consumer factory threads the broker-cancelled short-circuit through, so a permanent
    /// broker-cancellation flips Unhealthy without waiting out the grace window.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectBus(
        this IHealthChecksBuilder builder,
        string name,
        Func<IServiceProvider, IBus> busFactory,
        TimeSpan recoveryGraceWindow,
        TimeProvider? timeProvider = null,
        Func<IServiceProvider, IConsumer?>? consumerFactory = null,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(busFactory);
        // Per-SP cache so M4's _lastHealthyTicks accumulates across probes; M6's
        // rebuild contract preserved via ConditionalWeakTable's GC semantics.
        var cache = new PerProviderCache<BusConsumingHealthCheck>(sp => new BusConsumingHealthCheck(
            busFactory(sp),
            consumerFactory?.Invoke(sp),
            recoveryGraceWindow,
            timeProvider ?? TimeProvider.System));
        return builder.Add(new HealthCheckRegistration(
            name,
            cache.Resolve,
            failureStatus,
            tags,
            timeout));
    }

    // ── Consumer ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a health check that reports Healthy when the consumer connection
    /// is open (<see cref="IConsumer.IsConnected"/>). Resolves <see cref="IConsumer"/>
    /// from the DI container via <see cref="ServiceProviderServiceExtensions.GetRequiredService{T}"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectConsumer(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-consumer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        => builder.AddServiceConnectConsumer(name,
            sp => sp.GetRequiredService<IConsumer>(),
            failureStatus, tags, timeout);

    /// <summary>
    /// Registers a consumer-connection health check resolving the consumer via a keyed-services key.
    /// Convenience wrapper over the factory overload for hosts using
    /// <see cref="ServiceProviderKeyedServiceExtensions"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectConsumer(
        this IHealthChecksBuilder builder,
        string name,
        object serviceKey,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        => builder.AddServiceConnectConsumer(name,
            sp => sp.GetRequiredKeyedService<IConsumer>(serviceKey),
            failureStatus, tags, timeout);

    /// <summary>
    /// Registers a consumer-connection health check resolving the consumer via a factory function.
    /// Use this for non-DI-resolved consumers or for custom keyed-services patterns.
    /// The factory runs on every probe; the underlying <see cref="IConsumer"/> singleton
    /// is cached by the supplied <see cref="IServiceProvider"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectConsumer(
        this IHealthChecksBuilder builder,
        string name,
        Func<IServiceProvider, IConsumer> consumerFactory,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(consumerFactory);
        // Per-SP cache; see PerProviderCache xmldoc for the M4+M6 composition rationale.
        var cache = new PerProviderCache<ConsumerConnectionHealthCheck>(
            sp => new ConsumerConnectionHealthCheck(consumerFactory(sp)));
        return builder.Add(new HealthCheckRegistration(
            name,
            cache.Resolve,
            failureStatus,
            tags,
            timeout));
    }

    /// <summary>
    /// Registers a consumer-connection health check with a configurable recovery-grace window
    /// and optional <see cref="TimeProvider"/>. Use this overload when the host needs
    /// deterministic time control (tests with FakeTimeProvider) or a non-default grace window.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectConsumer(
        this IHealthChecksBuilder builder,
        string name,
        Func<IServiceProvider, IConsumer> consumerFactory,
        TimeSpan recoveryGraceWindow,
        TimeProvider? timeProvider = null,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(consumerFactory);
        // Per-SP cache so M4's _lastHealthyTicks accumulates across probes.
        var cache = new PerProviderCache<ConsumerConnectionHealthCheck>(sp => new ConsumerConnectionHealthCheck(
            consumerFactory(sp),
            recoveryGraceWindow,
            timeProvider ?? TimeProvider.System));
        return builder.Add(new HealthCheckRegistration(
            name,
            cache.Resolve,
            failureStatus,
            tags,
            timeout));
    }

    // ── Producer ────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a health check that reports Healthy when the producer connection
    /// is open (<see cref="IProducer.IsHealthy"/>). Resolves <see cref="IProducer"/>
    /// from the DI container via <see cref="ServiceProviderServiceExtensions.GetRequiredService{T}"/>.
    /// </summary>
    /// <remarks>
    /// The producer connects lazily on the first publish/send call. Hosts that
    /// do not publish at startup should not register this check on a readiness
    /// tag — it would report Unhealthy until the first outbound message.
    /// </remarks>
    public static IHealthChecksBuilder AddServiceConnectProducer(
        this IHealthChecksBuilder builder,
        string name = "serviceconnect-producer",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        => builder.AddServiceConnectProducer(name,
            sp => sp.GetRequiredService<IProducer>(),
            failureStatus, tags, timeout);

    /// <summary>
    /// Registers a producer-connection health check resolving the producer via a keyed-services key.
    /// Convenience wrapper over the factory overload for hosts using
    /// <see cref="ServiceProviderKeyedServiceExtensions"/>.
    /// </summary>
    public static IHealthChecksBuilder AddServiceConnectProducer(
        this IHealthChecksBuilder builder,
        string name,
        object serviceKey,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        => builder.AddServiceConnectProducer(name,
            sp => sp.GetRequiredKeyedService<IProducer>(serviceKey),
            failureStatus, tags, timeout);

    /// <summary>
    /// Registers a producer-connection health check resolving the producer via a factory function.
    /// Use this for non-DI-resolved producers or for custom keyed-services patterns.
    /// The factory runs on every probe; the underlying <see cref="IProducer"/> singleton
    /// is cached by the supplied <see cref="IServiceProvider"/>.
    /// </summary>
    /// <remarks>
    /// The producer connects lazily on the first publish/send call. Hosts that
    /// do not publish at startup should not register this check on a readiness
    /// tag — it would report Unhealthy until the first outbound message.
    /// </remarks>
    public static IHealthChecksBuilder AddServiceConnectProducer(
        this IHealthChecksBuilder builder,
        string name,
        Func<IServiceProvider, IProducer> producerFactory,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(producerFactory);
        // Producer check has no instance state today (no grace window) but caching
        // for symmetry: rebuilt SP gets a fresh check; per-SP probes share one wrapper.
        var cache = new PerProviderCache<ProducerConnectionHealthCheck>(
            sp => new ProducerConnectionHealthCheck(producerFactory(sp)));
        return builder.Add(new HealthCheckRegistration(
            name,
            cache.Resolve,
            failureStatus,
            tags,
            timeout));
    }
}
