namespace ServiceConnect.Interfaces;

/// <summary>
/// Enumerates the saga data types registered with the bus. Used by persistence
/// providers that need to pre-create per-saga structures (e.g. Mongo unique
/// CorrelationId indexes) at startup.
/// </summary>
public interface IProcessManagerTypeRegistry
{
    /// <summary>
    /// All saga data types currently registered. Implementations should return a
    /// snapshot — callers may iterate freely without locking.
    /// </summary>
    IEnumerable<Type> SagaDataTypes { get; }
}
