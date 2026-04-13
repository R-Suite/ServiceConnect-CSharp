namespace ServiceConnect.Interfaces;

public sealed class ProcessManagerToMessageMap
{
    public required Func<object, object> MessageProp { get; set; }
    public required Type MessageType { get; set; }
    public Dictionary<string, Type> PropertiesHierarchy { get; set; } = [];
}
