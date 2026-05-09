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
        // Pre-fix this method captured a `cached` field via closure and used
        // LazyInitializer.EnsureInitialized to construct the check once per registration.
        // The closure outlives the IServiceProvider that resolved the original IBus, so a
        // host that rebuilds its provider (test rigs, hot-reload, multi-tenant patterns) saw
        // probes against the OLD disposed bus from a stale check instance.
        //
        // Resolve fresh from the supplied sp on every probe. The IBus singleton is itself
        // cached by IServiceProvider; the only repeat alloc is the BusConsumingHealthCheck
        // wrapper, which is ~24 bytes — trivial relative to the rest of the probe path.
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new BusConsumingHealthCheck(busFactory(sp)),
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
        // See the AddServiceConnectBus overload for the pre-fix closure-cache rationale.
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new ConsumerConnectionHealthCheck(consumerFactory(sp)),
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
        // See the AddServiceConnectBus overload for the pre-fix closure-cache rationale.
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new ProducerConnectionHealthCheck(producerFactory(sp)),
            failureStatus,
            tags,
            timeout));
    }
}
