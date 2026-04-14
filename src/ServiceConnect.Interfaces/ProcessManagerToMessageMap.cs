namespace ServiceConnect.Interfaces;

public sealed class ProcessManagerToMessageMap
{
    public required Func<object, object> MessageProp { get; init; }
    public required Type MessageType { get; init; }
    public IReadOnlyDictionary<string, Type> PropertiesHierarchy { get; init; } = new Dictionary<string, Type>();
}
