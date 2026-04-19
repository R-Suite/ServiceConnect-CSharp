namespace ServiceConnect.Interfaces;

/// <summary>
/// Marker interface for internal handler registries that need eager initialization.
/// Implementations are resolved during startup to trigger validation of handler configurations.
/// </summary>
public interface IHandlerRegistry
{
}
