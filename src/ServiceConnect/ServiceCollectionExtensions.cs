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
            handlerReferences = HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies());
        else
            handlerReferences = [];

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

        var registry = new MessageTypeRegistry();
        foreach (var handlerRef in handlerReferences)
        {
            registry.Register(handlerRef.MessageType);
        }
        services.TryAddSingleton<IMessageTypeRegistry>(registry);

        // Apply additional registrations from builder extensions (e.g., persistence providers)
        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        // Bus
        services.TryAddSingleton<IBus, Bus>();

        // Hosted service for auto-start consuming
        services.AddHostedService<BusHostedService>();

        // Hosted service for process manager timeout polling
        services.AddHostedService<ProcessManagerTimeoutService>();

        return services;
    }
}
