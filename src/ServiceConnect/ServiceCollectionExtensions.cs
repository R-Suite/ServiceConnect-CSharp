using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;

namespace ServiceConnect;

/// <summary>
/// Extension methods for registering ServiceConnect services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
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
        var builder = new ServiceConnectBuilder();
        configure(builder);

        RegisterConfiguration(services, builder);
        RegisterCoreServices(services);
        RegisterProcessors(services);

        var handlerReferences = GetHandlerReferences(builder);
        RegisterHandlers(services, handlerReferences);

        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        ValidateSendMessageMiddlewareLifetimes(services, builder.BusConfig.Pipeline.SendMessageMiddleware);
        ValidateInboundMiddlewareAndFilterRegistrations(services, builder.BusConfig.Pipeline);
        RegisterBus(services);

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
        services.TryAddSingleton<IMessageSerializer, NewtonsoftJsonMessageSerializer>();
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

    private static void RegisterRequestReplyManager(IServiceCollection services)
    {
        // Check whether the caller has pre-registered a custom IRequestReplyManager.
        var existingRrmDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IRequestReplyManager));
        var existingRsrrmDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IReplyStatusRequestReplyManager));

        if (existingRrmDescriptor is null)
        {
            // Reverse split-brain guard: if the caller pre-registered a custom
            // IReplyStatusRequestReplyManager without also pre-registering IRequestReplyManager,
            // the two interfaces would resolve to different instances — reply tracking would
            // use the custom impl but outgoing-request dispatch would use the stock one.
            if (existingRsrrmDescriptor is not null)
            {
                throw new InvalidOperationException(
                    $"A custom '{nameof(IReplyStatusRequestReplyManager)}' has been registered "
                    + $"but '{nameof(IRequestReplyManager)}' has not been registered. "
                    + "Both interfaces must resolve to the same instance so that outgoing requests and "
                    + "incoming reply tracking are in sync. Register IRequestReplyManager as "
                    + "a forwarding factory before calling AddServiceConnect, e.g.: "
                    + "services.AddSingleton<IRequestReplyManager>(sp => (IRequestReplyManager)sp.GetRequiredService<IReplyStatusRequestReplyManager>());");
            }

            // No custom registration — use the stock concrete type for both interfaces.
            services.TryAddSingleton<RequestReplyManager>();
            services.TryAddSingleton<IRequestReplyManager>(sp => sp.GetRequiredService<RequestReplyManager>());
            services.TryAddSingleton<IReplyStatusRequestReplyManager>(sp => sp.GetRequiredService<RequestReplyManager>());
            return;
        }

        // A custom IRequestReplyManager has been registered. Both the public contract
        // (used by Bus to dispatch requests) and the internal contract (used by
        // ReplyProcessor to correlate incoming replies) must resolve to the same
        // instance — if they don't, replies are silently dropped.
        //
        // Determine the concrete implementation type so we can verify it also
        // implements IReplyStatusRequestReplyManager. For factory-based registrations
        // where the type cannot be statically inspected, the caller must pre-register
        // IReplyStatusRequestReplyManager themselves (TryAdd below will honour it).
        var implType = existingRrmDescriptor.ImplementationType
            ?? existingRrmDescriptor.ImplementationInstance?.GetType();

        var replyStatusAlreadyRegistered = services.Any(d => d.ServiceType == typeof(IReplyStatusRequestReplyManager));

        if (!replyStatusAlreadyRegistered)
        {
            if (implType is null)
            {
                // Factory-based registration and no IReplyStatusRequestReplyManager present.
                throw new InvalidOperationException(
                    $"A custom '{nameof(IRequestReplyManager)}' has been registered via a factory, "
                    + $"but '{nameof(IReplyStatusRequestReplyManager)}' has not been registered. "
                    + "Both interfaces must resolve to the same instance so that outgoing requests and "
                    + "incoming reply tracking are in sync. Register IReplyStatusRequestReplyManager as "
                    + "a forwarding factory before calling AddServiceConnect, e.g.: "
                    + "services.AddSingleton<IReplyStatusRequestReplyManager>(sp => (IReplyStatusRequestReplyManager)sp.GetRequiredService<IRequestReplyManager>());");
            }

            if (!typeof(IReplyStatusRequestReplyManager).IsAssignableFrom(implType))
            {
                throw new InvalidOperationException(
                    $"A custom '{nameof(IRequestReplyManager)}' has been registered but its implementation "
                    + $"('{implType.FullName}') does not also implement '{nameof(IReplyStatusRequestReplyManager)}'. "
                    + "Both interfaces must be implemented by the same type so that outgoing requests and "
                    + "incoming reply tracking use the same instance. Either remove the custom registration "
                    + "and use the built-in RequestReplyManager, or implement both interfaces on your custom type "
                    + "and register IReplyStatusRequestReplyManager as a forwarding factory to the same instance.");
            }

            // The caller's impl covers both interfaces. Wire IReplyStatusRequestReplyManager
            // to the same resolved instance so there is exactly one object in play.
            services.TryAddSingleton<IReplyStatusRequestReplyManager>(sp =>
                (IReplyStatusRequestReplyManager)sp.GetRequiredService<IRequestReplyManager>());
        }

        // IReplyStatusRequestReplyManager is either already registered by the caller or
        // was just wired above — nothing more to do for the custom registration path.
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

    private static void RegisterHandlers(IServiceCollection services, IList<HandlerReference> handlerReferences)
    {
        RegisterHandlerRegistries(services);

        foreach (var handlerRef in handlerReferences)
        {
            RegisterHandlerType(services, handlerRef);
        }

        services.TryAddSingleton<IList<HandlerReference>>(handlerReferences);

        services.TryAddSingleton<IMessageTypeRegistry>(sp =>
        {
            var registry = new MessageTypeRegistry();
            foreach (var handlerRef in sp.GetRequiredService<IList<HandlerReference>>())
                registry.Register(handlerRef.MessageType);
            return registry;
        });
    }

    private static void RegisterBus(IServiceCollection services)
    {
        services.TryAddSingleton<IRegistryInitializer, Services.RegistryInitializer>();
        services.TryAddSingleton<BusAccessor>();
        // Resolve Lazy<IBus> through the accessor rather than capturing the root
        // IServiceProvider — capturing the root SP inside the factory risks deadlock
        // if any transitive dependency dereferences Value during Bus construction.
        services.TryAddSingleton(sp => new Lazy<IBus>(() => sp.GetRequiredService<BusAccessor>().GetOrThrow()));
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
                sp.GetRequiredService<IList<HandlerReference>>(),
                sp.GetRequiredService<IPipelineConfiguration>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ConsumeScopeAccessor>(),
                sp.GetService<IConsumer>(),
                sp.GetService<IProducer>(),
                timeoutStore: sp.GetService<ITimeoutStore>(),
                consumeContextAccessor: sp.GetRequiredService<ConsumeContextAccessor>());
            sp.GetRequiredService<BusAccessor>().Set(bus);
            return bus;
        });
        services.AddSingleton<IHostedService, BusHostedService>();
        services.AddSingleton<IHostedService>(sp =>
            new ProcessManagerTimeoutService(
                sp.GetRequiredService<IBusConfiguration>(),
                sp.GetRequiredService<Lazy<IBus>>(),
                sp.GetService<ITimeoutStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ProcessManagerTimeoutService>>()));
    }

    private static void ValidateSendMessageMiddlewareLifetimes(IServiceCollection services, IEnumerable<Type> middlewareTypes)
    {
        foreach (var middlewareType in middlewareTypes)
        {
            var descriptors = services
                .Where(descriptor => descriptor.ServiceType == middlewareType || descriptor.ImplementationType == middlewareType)
                .ToArray();

            if (descriptors.Length == 0 || descriptors.Any(descriptor => descriptor.Lifetime != ServiceLifetime.Singleton))
            {
                throw new InvalidOperationException(
                    $"Send message middleware '{middlewareType.FullName}' must be registered as a singleton.");
            }
        }
    }

    // Inbound middleware and filters (both incoming and outgoing) run inside a
    // per-message DI scope, so any lifetime is permitted — but they still have to be
    // registered. Catch missing registrations at startup rather than letting them
    // surface as opaque DI resolution failures when the first message arrives.
    private static void ValidateInboundMiddlewareAndFilterRegistrations(IServiceCollection services, IPipelineConfiguration pipeline)
    {
        ValidateTypesRegistered(services, pipeline.MessageProcessingMiddleware, "Message processing middleware");
        ValidateTypesRegistered(services, pipeline.BeforeConsumingFilters, "Before-consuming filter");
        ValidateTypesRegistered(services, pipeline.AfterConsumingFilters, "After-consuming filter");
        ValidateTypesRegistered(services, pipeline.OutgoingFilters, "Outgoing filter");
    }

    private static void ValidateTypesRegistered(IServiceCollection services, IReadOnlyList<Type> types, string role)
    {
        foreach (var type in types)
        {
            var registered = services.Any(d => d.ServiceType == type || d.ImplementationType == type);
            if (!registered)
                throw new InvalidOperationException(
                    $"{role} '{type.FullName}' is referenced by the pipeline but is not registered in the service collection. "
                    + "Register the type via services.AddScoped/AddTransient/AddSingleton before calling AddServiceConnect.");
        }
    }

    private static void RegisterHandlerRegistries(IServiceCollection services)
    {
        services.TryAddSingleton<Services.Processors.ProcessManagerHandlerRegistry>(sp => new Services.Processors.ProcessManagerHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.ProcessManagerHandlerRegistry>>()));
        services.AddSingleton<IHandlerRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>());

        // Message-handler descriptor registry (eagerly built, singleton)
        services.TryAddSingleton<Services.Processors.MessageHandlerRegistry>(sp => new Services.Processors.MessageHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.MessageHandlerRegistry>>()));
        services.AddSingleton<IHandlerRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.MessageHandlerRegistry>());

        // Stream-handler descriptor registry (eagerly built, singleton)
        services.TryAddSingleton<Services.Processors.StreamHandlerRegistry>(sp => new Services.Processors.StreamHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.StreamHandlerRegistry>>()));
        services.AddSingleton<IHandlerRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.StreamHandlerRegistry>());

        // Aggregator descriptor registry (eagerly built, materializes each aggregator once to capture BatchSize/Timeout)
        services.TryAddSingleton<Services.Processors.AggregatorRegistry>(sp => new Services.Processors.AggregatorRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.AggregatorRegistry>>()));
        services.AddSingleton<IHandlerRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.AggregatorRegistry>());
    }

    private static IList<HandlerReference> GetHandlerReferences(ServiceConnectBuilder builder)
    {
        // Explicit ScanAssemblies(...) list takes precedence — it represents
        // "scan exactly these assemblies" and must not be overridden by the
        // global ScanForMessageHandlers=false flag.
        // When ScanForMessageHandlers=false, assemblies supplied via ScanAssemblies(...)
        // are still scanned; only the fallback AppDomain scan is suppressed.
        if (builder.ScanAssembliesList.Count > 0)
            return HandlerScanner.ScanForHandlers(builder.ScanAssembliesList.ToArray());

        if (!builder.BusConfig.ScanForMessageHandlers)
            return [];

        return HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies());
    }

    private static void RegisterHandlerType(IServiceCollection services, HandlerReference handlerRef)
    {
        var handlerType = handlerRef.HandlerType;

        // Handlers must never be singletons — the Context property and pooled IConsumeContext
        // are mutated per-message and would race across concurrent dispatch on a shared instance.
        // Transient is the safe default; we reject any caller who pre-registered the handler
        // as a singleton rather than silently co-existing two DI lifetimes.
        foreach (var descriptor in services.Where(d => d.ImplementationType == handlerType).ToArray())
        {
            if (descriptor.Lifetime == ServiceLifetime.Singleton)
                throw new InvalidOperationException(
                    $"Message handler '{handlerType.FullName}' must not be registered as a singleton. "
                    + "Handler instances hold per-message IConsumeContext state and must be transient or scoped. "
                    + "Remove the singleton registration and let AddServiceConnect register the handler as transient.");
        }

        // TryAddEnumerable dedupes on (ServiceType, ImplementationType) regardless of
        // lifetime, so a caller who pre-registered the handler as transient or scoped
        // is honored instead of producing a second descriptor. HandlerProcessor resolves
        // via GetServices(...), so a duplicate descriptor translates directly into the
        // same message being dispatched to two separately-constructed handler instances.
        //
        // The stricter guard below also covers factory-registered singletons (where
        // ImplementationType==null so TryAddEnumerable would not deduplicate): if ANY
        // descriptor already answers the handler interface, the user's registration is
        // authoritative and we skip the scan-registered transient entirely.
        var messageHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                && i.GetGenericArguments()[0] == handlerRef.MessageType);
        if (messageHandlerInterface != null)
        {
            if (services.Any(d => d.ServiceType == messageHandlerInterface))
                return; // user-registered handler exists; respect their registration
            services.TryAddEnumerable(ServiceDescriptor.Transient(messageHandlerInterface, handlerType));
            return;
        }

        var processHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                && i.GetGenericArguments()[1] == handlerRef.MessageType);
        if (processHandlerInterface != null)
        {
            services.TryAddEnumerable(ServiceDescriptor.Transient(processHandlerInterface, handlerType));
            return;
        }

        var streamHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
                && i.GetGenericArguments()[0] == handlerRef.MessageType);
        if (streamHandlerInterface != null)
        {
            services.TryAddEnumerable(ServiceDescriptor.Transient(streamHandlerInterface, handlerType));
            return;
        }

        if (handlerType.BaseType is { IsGenericType: true } baseType
            && baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
        {
            services.TryAddEnumerable(ServiceDescriptor.Transient(baseType, handlerType));
        }
    }
}
