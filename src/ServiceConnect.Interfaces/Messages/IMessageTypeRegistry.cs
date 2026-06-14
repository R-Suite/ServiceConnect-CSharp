using System.Diagnostics.CodeAnalysis;

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
    bool TryResolve(string typeName, [MaybeNullWhen(false)] out Type type);

    /// <summary>
    /// Registers a type so it can later be resolved by name.
    /// </summary>
    void Register(Type type);

    /// <summary>
    /// Returns a point-in-time snapshot of the registered type-name keys (both
    /// <see cref="Type.FullName"/> and <see cref="Type.AssemblyQualifiedName"/> entries).
    /// Used by persistors that want to filter stored records to those whose CLR type
    /// is currently resolvable (e.g. <c>$in</c> filters in the Mongo aggregator
    /// persistor) without materialising and discarding documents that would resolve
    /// to a missing type. The snapshot is detached from the registry; subsequent
    /// <see cref="Register"/> calls do not retroactively appear.
    /// </summary>
    /// <returns>The registered type-name keys.</returns>
    IReadOnlyCollection<string> AllRegisteredTypeNames();
}
