using System.Collections.Concurrent;
using System.Collections.Frozen;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

/// <summary>
/// Stores known message CLR types by their full and assembly-qualified names for dispatch-time lookup.
/// </summary>
public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _registeredTypes = new();
    private FrozenDictionary<string, Type>? _types;

    /// <inheritdoc />
    public bool TryResolve(string typeName, out Type type)
    {
        var types = Volatile.Read(ref _types);
        if (types == null)
        {
            types = _registeredTypes.ToFrozenDictionary();
            Interlocked.CompareExchange(ref _types, types, null);
            types = _types!;
        }

        return types.TryGetValue(typeName, out type!);
    }

    /// <inheritdoc />
    public void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Two types sharing a FullName (or, less commonly, an AssemblyQualifiedName)
        // must not silently overwrite each other — dispatch would then resolve to
        // whichever was registered last, which is load-order dependent. Reject the
        // collision with a clear signal. Re-registering the exact same Type is
        // idempotent.
        if (type.AssemblyQualifiedName is not null)
            AddOrReject(type.AssemblyQualifiedName, type);
        if (type.FullName is not null)
            AddOrReject(type.FullName, type);

        Volatile.Write(ref _types, null);
    }

    private void AddOrReject(string key, Type type)
    {
        var existing = _registeredTypes.GetOrAdd(key, type);
        if (existing != type)
        {
            throw new InvalidOperationException(
                $"Message type registration collision on key '{key}': already registered as '{existing.AssemblyQualifiedName}', cannot re-register as '{type.AssemblyQualifiedName}'.");
        }
    }
}
