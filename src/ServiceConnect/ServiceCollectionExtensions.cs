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
        services.TryAddSingleton<RequestReplyManager>();
        services.TryAddSingleton<IRequestReplyManager>(sp => sp.GetRequiredService<RequestReplyManager>());
        // Resolve the concrete RequestReplyManager directly rather than casting via
        // IRequestReplyManager. If a caller replaces IRequestReplyManager with a type
        // that does not also implement IReplyStatusRequestReplyManager, DI fails fast
        // with a clear error here instead of returning null at the callsite.
        services.TryAddSingleton<IReplyStatusRequestReplyManager>(sp => sp.GetRequiredService<RequestReplyManager>());
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
        if (!builder.BusConfig.ScanForMessageHandlers)
            return [];

        var assemblies = builder.ScanAssembliesList.Count > 0
            ? builder.ScanAssembliesList.ToArray()
            : AppDomain.CurrentDomain.GetAssemblies();
        return HandlerScanner.ScanForHandlers(assemblies);
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
        var messageHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                && i.GetGenericArguments()[0] == handlerRef.MessageType);
        if (messageHandlerInterface != null)
        {
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
