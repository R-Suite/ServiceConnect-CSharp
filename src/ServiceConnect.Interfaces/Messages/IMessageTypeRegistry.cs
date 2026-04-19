namespace ServiceConnect.Interfaces;

/// <summary>
/// A safe registry for resolving message type names to <see cref="Type"/> instances.
/// Unlike <c>Type.GetType</c>, this registry only resolves types that have been
/// explicitly registered, preventing arbitrary type activation from untrusted input.
/// </summary>
public interface IMessageTypeRegistry
{
    /// <summary>
    /// Attempts to resolve a previously registered type by its name.
    /// Returns <c>true</c> if the type was found; otherwise <c>false</c>.
    /// </summary>
    bool TryResolve(string typeName, out Type type);

    /// <summary>
    /// Registers a type so it can later be resolved by name.
    /// </summary>
    void Register(Type type);
}
