using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

internal sealed class RegistryInitializer(IEnumerable<IHandlerRegistry> registries) : IRegistryInitializer
{
    private readonly IEnumerable<IHandlerRegistry> _registries = registries ?? throw new ArgumentNullException(nameof(registries));

    public void Initialize()
    {
        // Force-enumerate all handler registries so they are eagerly constructed.
        // This validates handler registrations at startup without storing them in Bus.
        // The actual triggering of construction happens via DI when the IEnumerable is materialized.
        foreach (var _ in _registries)
        {
            // Iteration forces DI to resolve each registry, triggering validation.
        }
    }
}
