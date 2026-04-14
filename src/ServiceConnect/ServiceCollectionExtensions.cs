using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Configuration
        services.TryAddSingleton<IBusConfiguration>(builder.BusConfig);
        services.TryAddSingleton<ITransportConfiguration>(builder.BusConfig.Transport);
        services.TryAddSingleton<IQueueConfiguration>(builder.BusConfig.Queues);
        services.TryAddSingleton<IPersistenceConfiguration>(builder.BusConfig.Persistence);
        services.TryAddSingleton<IPipelineConfiguration>(builder.BusConfig.Pipeline);

        // Core services
        services.TryAddSingleton<IMessageSerializer, NewtonsoftJsonMessageSerializer>();
        services.TryAddSingleton<IFilterPipeline, FilterPipeline>();
        services.TryAddSingleton<IRequestReplyManager, RequestReplyManager>();
        services.TryAddSingleton<ISendMessagePipeline, SendMessagePipeline>();

        // Process manager descriptor registry (eagerly built, singleton)
        // Factory required because the ctor is internal (same-assembly access only)
        services.TryAddSingleton<Services.Processors.ProcessManagerHandlerRegistry>(sp => new Services.Processors.ProcessManagerHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.ProcessManagerHandlerRegistry>>()));

        // Message-handler descriptor registry (eagerly built, singleton)
        services.TryAddSingleton<Services.Processors.MessageHandlerRegistry>(sp => new Services.Processors.MessageHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.MessageHandlerRegistry>>()));

        // Stream-handler descriptor registry (eagerly built, singleton)
        services.TryAddSingleton<Services.Processors.StreamHandlerRegistry>(sp => new Services.Processors.StreamHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.StreamHandlerRegistry>>()));

        // Aggregator descriptor registry (eagerly built, materializes each aggregator once to capture BatchSize/Timeout)
        services.TryAddSingleton<Services.Processors.AggregatorRegistry>(sp => new Services.Processors.AggregatorRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.AggregatorRegistry>>()));

        // Message processors (order matters: ReplyProcessor first, then HandlerProcessor last)
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

        // Message dispatcher and handler scanning
        services.TryAddSingleton<IMessageDispatcher, MessageDispatcher>();

        IList<HandlerReference> handlerReferences;
        if (builder.BusConfig.ScanForMessageHandlers)
        {
            // Prefer explicit assemblies from the builder; fall back to the loaded AppDomain
            // only when no assemblies were supplied. Explicit registration avoids
            // the static global dependency that breaks test isolation (A-11).
            var assemblies = builder.ScanAssembliesList.Count > 0
                ? builder.ScanAssembliesList.ToArray()
                : AppDomain.CurrentDomain.GetAssemblies();
            handlerReferences = HandlerScanner.ScanForHandlers(assemblies);
        }
        else
        {
            handlerReferences = [];
        }

        foreach (var handlerRef in handlerReferences)
        {
            var handlerType = handlerRef.HandlerType;

            // IMessageHandler<T>
            var messageHandlerInterface = handlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                    && i.GetGenericArguments()[0] == handlerRef.MessageType);
            if (messageHandlerInterface != null)
            {
                services.AddTransient(messageHandlerInterface, handlerType);
                continue;
            }

            // IProcessHandler<TData, TMessage>
            var processHandlerInterface = handlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                    && i.GetGenericArguments()[1] == handlerRef.MessageType);
            if (processHandlerInterface != null)
            {
                services.AddTransient(processHandlerInterface, handlerType);
                continue;
            }

            // IStreamHandler<T>
            var streamHandlerInterface = handlerType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
                    && i.GetGenericArguments()[0] == handlerRef.MessageType);
            if (streamHandlerInterface != null)
            {
                services.AddTransient(streamHandlerInterface, handlerType);
                continue;
            }

            // Aggregator<T> subclass
            if (handlerType.BaseType is { IsGenericType: true } baseType
                && baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
            {
                services.AddTransient(baseType, handlerType);
            }
        }

        services.TryAddSingleton<IList<HandlerReference>>(handlerReferences);

        services.TryAddSingleton<IMessageTypeRegistry>(sp =>
        {
            var registry = new MessageTypeRegistry();
            foreach (var handlerRef in sp.GetRequiredService<IList<HandlerReference>>())
                registry.Register(handlerRef.MessageType);
            return registry;
        });

        // Apply additional registrations from builder extensions (e.g., persistence providers)
        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        // Lazy<IBus> breaks the circular dependency: Bus → IMessageDispatcher → processors → IBus.
        // Processors receive a Lazy<IBus> so the IBus singleton is only resolved after construction
        // completes, avoiding a DI cycle while still caching the resolved instance.
        services.TryAddSingleton(sp => new Lazy<IBus>(() => sp.GetRequiredService<IBus>()));

        // Bus — uses a factory so that DI can resolve the internal ctor.
        // Force-resolve the four handler registries so they are eagerly constructed
        // (validates handler registrations at startup) without storing them in Bus.
        services.TryAddSingleton<IBus>(sp =>
        {
            _ = sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>();
            _ = sp.GetRequiredService<Services.Processors.MessageHandlerRegistry>();
            _ = sp.GetRequiredService<Services.Processors.StreamHandlerRegistry>();
            _ = sp.GetRequiredService<Services.Processors.AggregatorRegistry>();

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
                sp.GetService<IProducer>());
        });

        // Hosted service for auto-start consuming
        services.AddHostedService<BusHostedService>();

        // Hosted service for process manager timeout polling
        services.AddHostedService<ProcessManagerTimeoutService>();

        return services;
    }
}
