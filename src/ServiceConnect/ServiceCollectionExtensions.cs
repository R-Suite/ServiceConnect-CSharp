using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;
using ServiceConnect.Services.Processors;

namespace ServiceConnect;

public static class ServiceCollectionExtensions
{
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
        services.TryAdd(new ServiceDescriptor(
            typeof(IReplyStatusRequestReplyManager),
            sp => (sp.GetService<IRequestReplyManager>() as IReplyStatusRequestReplyManager)!,
            ServiceLifetime.Singleton));
        services.TryAddSingleton<ISendMessagePipeline, SendMessagePipeline>();
        services.TryAddSingleton<ConsumeContextPool>();
        services.TryAddSingleton<ConsumeContextAccessor>();
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
        services.TryAddSingleton(sp => new Lazy<IBus>(() => sp.GetRequiredService<IBus>()));
        services.TryAddSingleton<IBus>(sp =>
        {
            sp.GetRequiredService<IRegistryInitializer>().Initialize();

            return new Bus(
                sp.GetRequiredService<IMessageSerializer>(),
                sp.GetRequiredService<IFilterPipeline>(),
                sp.GetRequiredService<ISendMessagePipeline>(),
                sp.GetRequiredService<IRequestReplyManager>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Bus>>(),
                sp.GetRequiredService<IQueueConfiguration>(),
                sp.GetRequiredService<IMessageDispatcher>(),
                sp.GetRequiredService<IList<HandlerReference>>(),
                sp.GetRequiredService<IPipelineConfiguration>(),
                sp.GetService<IConsumer>(),
                sp.GetService<IProducer>(),
                timeoutStore: sp.GetService<ITimeoutStore>(),
                consumeContextAccessor: sp.GetRequiredService<ConsumeContextAccessor>());
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

        var messageHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                && i.GetGenericArguments()[0] == handlerRef.MessageType);
        if (messageHandlerInterface != null)
        {
            services.AddTransient(messageHandlerInterface, handlerType);
            return;
        }

        var processHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                && i.GetGenericArguments()[1] == handlerRef.MessageType);
        if (processHandlerInterface != null)
        {
            services.AddTransient(processHandlerInterface, handlerType);
            return;
        }

        var streamHandlerInterface = handlerType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType
                && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
                && i.GetGenericArguments()[0] == handlerRef.MessageType);
        if (streamHandlerInterface != null)
        {
            services.AddTransient(streamHandlerInterface, handlerType);
            return;
        }

        if (handlerType.BaseType is { IsGenericType: true } baseType
            && baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
        {
            services.AddTransient(baseType, handlerType);
        }
    }
}
