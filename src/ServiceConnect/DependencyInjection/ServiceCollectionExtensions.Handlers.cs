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
    private static IReadOnlyList<HandlerReference> GetHandlerReferences(ServiceConnectBuilder builder, out IReadOnlyList<HandlerScanWarning> warnings)
    {
        // Explicit ScanAssemblies(...) list takes precedence — it represents
        // "scan exactly these assemblies" and must not be overridden by the
        // global ScanForMessageHandlers=false flag.
        // When ScanForMessageHandlers=false, assemblies supplied via ScanAssemblies(...)
        // are still scanned; only the fallback AppDomain scan is suppressed.
        if (builder.ScanAssembliesList.Count > 0)
        {
            return HandlerScanner.ScanForHandlers([.. builder.ScanAssembliesList], out warnings);
        }

        if (!builder.BusConfig.ScanForMessageHandlers)
        {
            warnings = [];
            return [];
        }

        return HandlerScanner.ScanForHandlers(AppDomain.CurrentDomain.GetAssemblies(), out warnings);
    }

    private static void RegisterHandlers(IServiceCollection services, IReadOnlyList<HandlerReference> handlerReferences)
    {
        RegisterHandlerRegistries(services);

        // Snapshot the service types already present before the scan loop runs.
        // RegisterHandlerType uses this to distinguish user pre-registrations
        // (present in the snapshot) from scan-discovered handlers added by earlier
        // iterations of this loop (not in the snapshot).
        var preExistingServiceTypes = services.Select(d => d.ServiceType).ToHashSet();

        foreach (var handlerRef in handlerReferences)
        {
            RegisterHandlerType(services, handlerRef, preExistingServiceTypes);
        }

        services.TryAddSingleton<IReadOnlyList<HandlerReference>>(handlerReferences);

        services.TryAddSingleton<IMessageTypeRegistry>(sp =>
        {
            var registry = new MessageTypeRegistry();
            foreach (var handlerRef in sp.GetRequiredService<IReadOnlyList<HandlerReference>>())
            {
                registry.Register(handlerRef.MessageType);
            }

            return registry;
        });
    }

    private static void RegisterHandlerRegistries(IServiceCollection services)
    {
        services.TryAddSingleton<Services.Processors.ProcessManagerHandlerRegistry>(sp => new Services.Processors.ProcessManagerHandlerRegistry(
            sp.GetRequiredService<IReadOnlyList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.ProcessManagerHandlerRegistry>>()));
        // TryAddEnumerable with the typed factory overload (ServiceDescriptor.Singleton<TService, TImpl>(factory))
        // creates a descriptor whose ImplementationType is ProcessManagerHandlerRegistry, not null.
        // TryAddEnumerable deduplicates on (ServiceType, ImplementationType), so this is idempotent
        // on repeated calls while the factory still forwards to the shared concrete singleton.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHandlerRegistry, Services.Processors.ProcessManagerHandlerRegistry>(
            sp => sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>()));

        // Register the same instance under IProcessManagerTypeRegistry so persistence
        // providers that need to pre-create per-saga structures (e.g. Mongo unique
        // CorrelationId indexes) at startup can enumerate the saga data types.
        services.TryAddSingleton<IProcessManagerTypeRegistry>(sp =>
            sp.GetRequiredService<Services.Processors.ProcessManagerHandlerRegistry>());

        // Message-handler descriptor registry (eagerly built, singleton)
        services.TryAddSingleton<Services.Processors.MessageHandlerRegistry>(sp => new Services.Processors.MessageHandlerRegistry(
            sp.GetRequiredService<IReadOnlyList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.MessageHandlerRegistry>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHandlerRegistry, Services.Processors.MessageHandlerRegistry>(
            sp => sp.GetRequiredService<Services.Processors.MessageHandlerRegistry>()));

        // Stream-handler descriptor registry (eagerly built, singleton)
        services.TryAddSingleton<Services.Processors.StreamHandlerRegistry>(sp => new Services.Processors.StreamHandlerRegistry(
            sp.GetRequiredService<IReadOnlyList<HandlerReference>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.StreamHandlerRegistry>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHandlerRegistry, Services.Processors.StreamHandlerRegistry>(
            sp => sp.GetRequiredService<Services.Processors.StreamHandlerRegistry>()));

        // Aggregator descriptor registry (eagerly built, materializes each aggregator once to capture BatchSize/Timeout).
        // Resolves via IServiceScopeFactory so transient/scoped aggregator dependencies are not
        // held captive by the root provider for the host's lifetime — the temporary scope is
        // disposed inside the registry constructor.
        services.TryAddSingleton<Services.Processors.AggregatorRegistry>(sp => new Services.Processors.AggregatorRegistry(
            sp.GetRequiredService<IReadOnlyList<HandlerReference>>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.Processors.AggregatorRegistry>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHandlerRegistry, Services.Processors.AggregatorRegistry>(
            sp => sp.GetRequiredService<Services.Processors.AggregatorRegistry>()));
    }

    /// <remarks>
    /// <para>
    /// Each <see cref="HandlerReference"/> carries an <see cref="HandlerInterfaceKind"/> discriminator
    /// that identifies which handler interface the reference was produced for. The registration
    /// loop dispatches each reference to only the relevant <c>TryAddEnumerable</c> call, so a
    /// class that implements both <c>IMessageHandler&lt;A&gt;</c> and <c>IProcessHandler&lt;TData,A&gt;</c>
    /// produces two separate references and both interfaces are registered.
    /// </para>
    /// <para>
    /// For all four handler kinds, user pre-registrations are detected by checking
    /// <paramref name="preExistingServiceTypes"/> — a snapshot taken before the scan loop starts.
    /// If the service type for a handler kind (e.g. <c>IMessageHandler&lt;T&gt;</c>,
    /// <c>IProcessHandler&lt;TData,T&gt;</c>, <c>IStreamHandler&lt;T&gt;</c>, or <c>Aggregator&lt;T&gt;</c>)
    /// appears in that snapshot, the user registered it and the scan-discovered handler is suppressed.
    /// Descriptors added by earlier iterations of the scan loop itself are NOT in the snapshot, so
    /// multiple scan-discovered implementations of the same interface are all registered.
    /// <c>TryAddEnumerable</c> dedupes on <c>(ServiceType, ImplementationType)</c> so re-adding
    /// the same pair in a later scan pass is a safe no-op.
    /// </para>
    /// </remarks>
    private static void RegisterHandlerType(
        IServiceCollection services,
        HandlerReference handlerRef,
        IReadOnlySet<Type>? preExistingServiceTypes = null)
    {
        var handlerType = handlerRef.HandlerType;

        // Handlers must never be singletons — handler instances carry per-message
        // IConsumeContext state and would race across concurrent dispatches on a shared instance.
        // Transient is the safe default; reject any caller who pre-registered the handler
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

        // Each branch handles exactly the interface kind recorded in the reference.
        // TryAddEnumerable dedupes on (ServiceType, ImplementationType), so re-adding
        // the same descriptor from a second scan pass is a safe no-op.
        switch (handlerRef.InterfaceKind)
        {
            case HandlerInterfaceKind.MessageHandler:
            {
                var messageHandlerInterface = handlerType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IMessageHandler<>)
                        && i.GetGenericArguments()[0] == handlerRef.MessageType);
                if (messageHandlerInterface == null)
                {
                    return;
                }

                // If the service type was present before the scan loop started, the user
                // pre-registered a handler. Respect that registration and suppress the
                // scan-discovered one. Descriptors added by the scan loop itself are not
                // in preExistingServiceTypes, so multiple scan-discovered handlers for the
                // same interface all pass through; TryAddEnumerable deduplicates them by
                // (ServiceType, ImplementationType).
                if (preExistingServiceTypes?.Contains(messageHandlerInterface) == true)
                {
                    return;
                }

                services.TryAddEnumerable(ServiceDescriptor.Transient(messageHandlerInterface, handlerType));
                break;
            }

            case HandlerInterfaceKind.ProcessHandler:
            {
                var processHandlerInterface = handlerType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IProcessHandler<,>)
                        && i.GetGenericArguments()[1] == handlerRef.MessageType);
                if (processHandlerInterface == null)
                {
                    break;
                }

                // User pre-registration of IProcessHandler<TData, TMsg> takes precedence over
                // a scan-discovered handler for the same interface — same rule as MessageHandler.
                if (preExistingServiceTypes?.Contains(processHandlerInterface) == true)
                {
                    return;
                }

                services.TryAddEnumerable(ServiceDescriptor.Transient(processHandlerInterface, handlerType));
                break;
            }

            case HandlerInterfaceKind.StreamHandler:
            {
                var streamHandlerInterface = handlerType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType
                        && i.GetGenericTypeDefinition() == typeof(IStreamHandler<>)
                        && i.GetGenericArguments()[0] == handlerRef.MessageType);
                if (streamHandlerInterface == null)
                {
                    break;
                }

                // User pre-registration of IStreamHandler<T> takes precedence over
                // a scan-discovered handler for the same interface.
                if (preExistingServiceTypes?.Contains(streamHandlerInterface) == true)
                {
                    return;
                }

                services.TryAddEnumerable(ServiceDescriptor.Transient(streamHandlerInterface, handlerType));
                break;
            }

            case HandlerInterfaceKind.Aggregator:
            {
                if (handlerType.BaseType is { IsGenericType: true } baseType
                    && baseType.GetGenericTypeDefinition() == typeof(Aggregator<>))
                {
                    // User pre-registration of Aggregator<T> (the closed base type) takes
                    // precedence over a scan-discovered subclass for the same message type.
                    if (preExistingServiceTypes?.Contains(baseType) == true)
                    {
                        return;
                    }

                    services.TryAddEnumerable(ServiceDescriptor.Transient(baseType, handlerType));
                }

                break;
            }
        }
    }
}
