namespace ServiceConnect.Services;

/// <summary>
/// Eagerly initializes internal handler registries at startup to validate
/// handler configurations before the bus processes any messages.
/// Implementations perform registry resolution to trigger construction and validation.
/// </summary>
internal interface IRegistryInitializer
{
    /// <summary>
    /// Initializes all handler registries, triggering eager validation of handler configurations.
    /// Called automatically during Bus construction to fail fast on misconfigured handlers.
    /// </summary>
    void Initialize();
}
