using System.Collections.Concurrent;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Services;

public sealed class MessageTypeRegistry : IMessageTypeRegistry
{
    private readonly ConcurrentDictionary<string, Type> _types = new();

    public bool TryResolve(string typeName, out Type type)
        => _types.TryGetValue(typeName, out type!);

    public void Register(Type type)
    {
        if (type.AssemblyQualifiedName is not null)
            _types[type.AssemblyQualifiedName] = type;
        if (type.FullName is not null)
            _types[type.FullName] = type;
    }
}
