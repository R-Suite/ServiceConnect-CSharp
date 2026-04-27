using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceConnect.Interfaces;
using ServiceConnect.Services;

namespace ServiceConnect;

/// <summary>
/// Request/reply manager registration logic for <see cref="ServiceCollectionExtensions"/>.
/// Both <see cref="IRequestReplyManager"/> (caller-facing send) and
/// <see cref="IReplyStatusRequestReplyManager"/> (reply-correlation, used by
/// <c>ReplyProcessor</c>) must resolve to the same instance — otherwise outgoing requests
/// and incoming reply tracking diverge and replies are silently dropped. This class wires
/// the stock <see cref="RequestReplyManager"/> into both interfaces and refuses to start
/// when caller registrations would split-brain the two.
/// </summary>
public static partial class ServiceCollectionExtensions
{
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
}
