using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;

namespace ServiceConnect.DependencyInjection;

/// <summary>
/// Extension methods for registering ServiceConnect services with dependency injection.
/// Public entry point and the small bookkeeping registrations live here; handler scanning,
/// request/reply-manager wiring, and pipeline registration validation live in sibling
/// partial-class files for navigability.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core ServiceConnect services, handlers, and hosted services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">The callback used to configure the ServiceConnect builder.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddServiceConnect(
        this IServiceCollection services,
        Action<ServiceConnectBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Reject re-entry. ServiceConnect is a single-bus framework per IServiceCollection:
        // the first call wins via TryAddSingleton<IBusConfiguration> and the second call's
        // handler scan, BusConfig, and PipelineConfiguration would be silently dropped —
        // incoming messages of types from the second call would route as NotHandled and
        // ack-and-drop with no operator signal. Detect the second call by looking for any
        // existing IBusConfiguration registration (only AddServiceConnect registers it).
        // Note: IReadOnlyList<HandlerReference> is intentionally NOT in this check — tests
        // and some feature-flag patterns legitimately pre-register their own handler-reference
        // list before calling AddServiceConnect.
        if (services.Any(d => d.ServiceType == typeof(IBusConfiguration)))
        {
            throw new InvalidOperationException(
                "AddServiceConnect has already been called on this IServiceCollection. " +
                "ServiceConnect is single-bus per service collection; calling it twice would silently drop the second call's handler scan, BusConfig, and PipelineConfiguration. " +
                "Consolidate all feature-module ServiceConnect registrations into a single AddServiceConnect call, " +
                "or use a separate IServiceCollection per bus instance.");
        }

        var builder = new ServiceConnectBuilder();
        configure(builder);

        RegisterConfiguration(services, builder);
        RegisterCoreServices(services);
        RegisterProcessors(services);

        var handlerReferences = GetHandlerReferences(builder, out var scanWarnings);
        RegisterHandlers(services, handlerReferences);

        // Stash scan warnings so BusHostedService can replay them against the configured
        // ILogger at startup — the scan ran before DI was built, so any partial-scan
        // warnings (missing assemblies, broken plugins) would otherwise be dropped.
        services.AddSingleton<IReadOnlyList<Services.HandlerScanWarning>>(scanWarnings);

        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        ValidateSendMessageMiddlewareLifetimes(services, builder.BusConfig.Pipeline.SendMessageMiddleware);
        ValidateInboundMiddlewareAndFilterRegistrations(services, builder.BusConfig.Pipeline);
        RegisterBus(services);

        // Freeze the top-level BusConfiguration so post-registration mutations from code
        // that resolves IBusConfiguration from DI (or holds a captured builder reference)
        // throw rather than silently overriding the validated values that gate
        // DisposeTimeout / MaxRoutingSlipHops runtime invariants. Freeze runs AFTER all
        // additional registrations and validators so feature modules still have a chance
        // to extend the bus during AddServiceConnect.
        builder.BusConfig.Freeze();

        return services;
    }

    private static void RegisterConfiguration(IServiceCollection services, ServiceConnectBuilder builder)
    {
        services.TryAddSingleton<IBusConfiguration>(builder.BusConfig);
        services.TryAddSingleton<ITransportConfiguration>(builder.BusConfig.Transport);
        services.TryAddSingleton<IQueueConfiguration>(builder.BusConfig.Queues);
        services.TryAddSingleton<IPersistenceConfiguration>(builder.BusConfig.Persistence);
        services.TryAddSingleton<IPipelineConfiguration>(builder.BusConfig.Pipeline);

        services.TryAddSingleton(TimeProvider.System);
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton<IMessageSerializer, SystemTextJsonMessageSerializer>();
        services.TryAddSingleton<IFilterPipeline, FilterPipeline>();
        RegisterRequestReplyManager(services);
        services.TryAddSingleton<ISendMessagePipeline, SendMessagePipeline>();
        services.TryAddSingleton<ConsumeContextPool>();
        services.TryAddSingleton<ConsumeContextAccessor>();
        // The consume-scope accessor flows the current DI scope through AsyncLocal so
        // inbound filters, middleware, and processors resolve scoped services from the
        // per-message scope established by MessageDispatcher and outgoing filter sites.
        services.TryAddSingleton<ConsumeScopeAccessor>();
    }

    private static void RegisterProcessors(IServiceCollection services)
    {
        services.TryAddSingleton<ReplyProcessor>();
        services.TryAddSingleton<StreamProcessor>();
        services.TryAddSingleton<ProcessManagerProcessor>();
        services.TryAddSingleton<AggregatorProcessor>();
        services.TryAddSingleton<HandlerProcessor>();
        services.TryAddSingleton<IList<IMessageProcessor>>(sp =>
        [
            sp.GetRequiredService<ReplyProcessor>(),
            sp.GetRequiredService<StreamProcessor>(),
            sp.GetRequiredService<ProcessManagerProcessor>(),
            sp.GetRequiredService<AggregatorProcessor>(),
            sp.GetRequiredService<HandlerProcessor>()
        ]);
        services.TryAddSingleton<IMessageDispatcher, MessageDispatcher>();
    }

    private static void RegisterBus(IServiceCollection services)
    {
        services.TryAddSingleton<IRegistryInitializer, Services.RegistryInitializer>();
        services.TryAddSingleton<BusAccessor>();
        // Resolve Lazy<IBus> through the accessor rather than capturing the root
        // IServiceProvider — capturing the root SP inside the factory risks deadlock
        // if any transitive dependency dereferences Value during Bus construction.
        // Fallback: when a caller pre-registered IBus before AddServiceConnect ran, the
        // TryAddSingleton<IBus> below skips and BusAccessor.Set never fires; defer to the
        // container's IBus resolution so the deferred reference still works for that case.
        services.TryAddSingleton(sp => new Lazy<IBus>(() =>
        {
            var accessor = sp.GetRequiredService<BusAccessor>();
            return accessor.Bus ?? sp.GetRequiredService<IBus>();
        }));
        services.TryAddSingleton<IBus>(sp =>
        {
            sp.GetRequiredService<IRegistryInitializer>().Initialize();

            var bus = new Bus(
                sp.GetRequiredService<IMessageSerializer>(),
                sp.GetRequiredService<IFilterPipeline>(),
                sp.GetRequiredService<ISendMessagePipeline>(),
                sp.GetRequiredService<IRequestReplyManager>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Bus>>(),
                sp.GetRequiredService<IQueueConfiguration>(),
                sp.GetRequiredService<IMessageDispatcher>(),
                sp.GetRequiredService<IReadOnlyList<HandlerReference>>(),
                sp.GetRequiredService<IPipelineConfiguration>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ConsumeScopeAccessor>(),
                sp.GetService<IConsumer>(),
                sp.GetService<IProducer>(),
                timeoutStore: sp.GetService<ITimeoutStore>(),
                consumeContextAccessor: sp.GetRequiredService<ConsumeContextAccessor>(),
                busConfig: sp.GetRequiredService<IBusConfiguration>(),
                timeProvider: sp.GetService<TimeProvider>());
            sp.GetRequiredService<BusAccessor>().Set(bus);
            return bus;
        });
        // TryAddEnumerable so a second AddServiceConnect call (e.g. two feature modules
        // each calling it) does not start two BusHostedService instances — the second
        // StartConsumingAsync would throw "Already consuming" and kill host startup —
        // nor two ProcessManagerTimeoutService instances both polling the same store.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BusHostedService>());
        // TryAddEnumerable dedups by implementation type, so the factory-bound descriptor
        // needs a concrete TImplementation. Use the typed factory overload so a repeat
        // AddServiceConnect call doesn't double-register ProcessManagerTimeoutService.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProcessManagerTimeoutService>(sp =>
            new ProcessManagerTimeoutService(
                sp.GetRequiredService<IBusConfiguration>(),
                sp.GetRequiredService<Lazy<IBus>>(),
                sp.GetService<ITimeoutStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProcessManagerTimeoutService>>(),
                sp.GetService<TimeProvider>())));
    }
}
