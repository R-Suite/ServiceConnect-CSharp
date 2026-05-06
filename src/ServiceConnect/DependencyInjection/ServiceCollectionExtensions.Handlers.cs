using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;

namespace ServiceConnect.DependencyInjection;

/// <summary>
/// Handler-scanning and handler-registration parts of <see cref="ServiceCollectionExtensions"/>.
/// Discovers <see cref="IMessageHandler{T}"/> / <see cref="IProcessHandler{TData,TMessage}"/> /
/// <see cref="IStreamHandler{T}"/> / <see cref="Aggregator{T}"/> implementations and registers
/// them as transient (with a hard guard against caller-registered singletons), plus the
/// <see cref="MessageTypeRegistry"/> and the four per-handler-shape registries used by the
/// processor pipeline.
/// </summary>
public static partial class ServiceCollectionExtensions
{
    private static IList<HandlerReference> GetHandlerReferences(ServiceConnectBuilder builder)
    {
        // Explicit ScanAssemblies(...) list takes precedence — it represents
        // "scan exactly these assemblies" and must not be overridden by the
        // global ScanForMessageHandlers=false flag.
        // When ScanForMessageHandlers=false, assemblies supplied via ScanAssemblies(...)
        // are still scanned; only the fallback AppDomain scan is suppressed.
        if (builder.ScanAssembliesList.Count > 0)
        {
            return HandlerScanner.ScanForHandlers([.. builder.ScanAssembliesList]);
        }

        if (!builder.BusConfig.ScanForMessageHandlers)
        {
            return [];
        }

        return HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies());
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
            {
                registry.Register(handlerRef.MessageType);
            }

            return registry;
        });
    }

    private static void RegisterHandlerRegistries(IServiceCollection services)
    {
        services.TryAddSingleton<Services.Processors.ProcessManagerHandlerRegistry>(sp => new Services.Processors.ProcessManagerHandlerRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.ProcessManagerHandlerRegistry>>()));
        services.AddSingleton<IHandlerRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>());

        // Register the same instance under IProcessManagerTypeRegistry so persistence
        // providers that need to pre-create per-saga structures (e.g. Mongo unique
        // CorrelationId indexes) at startup can enumerate the saga data types.
        services.AddSingleton<IProcessManagerTypeRegistry>(sp =>
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

        // Aggregator descriptor registry (eagerly built, materializes each aggregator once to capture BatchSize/Timeout).
        // Resolves via IServiceScopeFactory so transient/scoped aggregator dependencies are not
        // held captive by the root provider for the host's lifetime — the temporary scope is
        // disposed inside the registry constructor.
        services.TryAddSingleton<Services.Processors.AggregatorRegistry>(sp => new Services.Processors.AggregatorRegistry(
            sp.GetRequiredService<IList<HandlerReference>>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.AggregatorRegistry>>()));
        services.AddSingleton<IHandlerRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.AggregatorRegistry>());
    }

    /// <remarks>
    /// If the DI container already contains a descriptor for <c>IMessageHandler&lt;T&gt;</c>
    /// (regardless of how it was registered — implementation type, instance, or factory),
    /// the scanner skips adding scan-discovered handlers for <c>T</c>. User registrations are
    /// authoritative; callers who want both a manually-registered handler and scan-discovered
    /// handlers for the same message type must register all of them explicitly.
    /// </remarks>
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
            {
                throw new InvalidOperationException(
                    $"Message handler '{handlerType.FullName}' must not be registered as a singleton. "
                    + "Handler instances hold per-message IConsumeContext state and must be transient or scoped. "
                    + "Remove the singleton registration and let AddServiceConnect register the handler as transient.");
            }
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
            {
                return; // user-registered handler exists; respect their registration
            }

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
