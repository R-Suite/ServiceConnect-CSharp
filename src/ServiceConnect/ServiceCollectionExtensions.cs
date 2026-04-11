using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Configuration;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.Services;

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

        // Message dispatcher and handler scanning
        services.TryAddSingleton<MessageDispatcher>();

        IList<HandlerReference> handlerReferences;
        if (builder.BusConfig.ScanForMessageHandlers)
            handlerReferences = HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies());
        else
            handlerReferences = new List<HandlerReference>();

        foreach (var handlerRef in handlerReferences)
        {
            var handlerInterfaceType = typeof(IMessageHandler<>).MakeGenericType(handlerRef.MessageType);
            services.AddTransient(handlerInterfaceType, handlerRef.HandlerType);
        }

        services.TryAddSingleton<IList<HandlerReference>>(handlerReferences);

        // Apply additional registrations from builder extensions (e.g., persistence providers)
        foreach (var registration in builder.AdditionalRegistrations)
        {
            registration(services);
        }

        // Bus
        services.TryAddSingleton<IBus, Bus>();

        return services;
    }
}
