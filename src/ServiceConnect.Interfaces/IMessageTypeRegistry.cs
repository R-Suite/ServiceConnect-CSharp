namespace ServiceConnect.Interfaces;

public interface IMessageTypeRegistry
{
    bool TryResolve(string typeName, out Type type);
    void Register(Type type);
}
