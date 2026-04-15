using System.Collections.Concurrent;
using System.Collections.Frozen;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _registeredTypes = new();
    private FrozenDictionary<string, Type>? _types;

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

    public void Register(Type type)
    {
        if (type.AssemblyQualifiedName is not null)
            _registeredTypes[type.AssemblyQualifiedName] = type;
        if (type.FullName is not null)
            _registeredTypes[type.FullName] = type;

        Volatile.Write(ref _types, null);
    }
}
