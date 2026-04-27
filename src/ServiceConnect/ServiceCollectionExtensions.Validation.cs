using Microsoft.Extensions.DependencyInjection;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect;

/// <summary>
/// Pipeline-registration validators for <see cref="ServiceCollectionExtensions"/>. Catches
/// missing DI registrations at startup so they surface as a clear error rather than as an
/// opaque resolution failure on the first message arrival, and enforces the singleton-only
/// lifetime constraint on <c>SendMessageMiddleware</c>.
/// </summary>
public static partial class ServiceCollectionExtensions
{
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
            {
                throw new InvalidOperationException(
                    $"{role} '{type.FullName}' is referenced by the pipeline but is not registered in the service collection. "
                    + "Register the type via services.AddScoped/AddTransient/AddSingleton before calling AddServiceConnect.");
            }
        }
    }
}
