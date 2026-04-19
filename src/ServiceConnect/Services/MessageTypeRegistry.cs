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
        if (type.AssemblyQualifiedName is not null)
            _registeredTypes[type.AssemblyQualifiedName] = type;
        if (type.FullName is not null)
            _registeredTypes[type.FullName] = type;

        Volatile.Write(ref _types, null);
    }
}
