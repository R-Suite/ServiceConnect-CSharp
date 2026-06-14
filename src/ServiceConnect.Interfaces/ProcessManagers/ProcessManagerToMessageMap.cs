namespace ServiceConnect.Interfaces;

/// <summary>
/// Describes how a message property maps onto process-manager state.
/// </summary>
public sealed class ProcessManagerToMessageMap
{
    /// <summary>
    /// Gets the compiled accessor used to read the mapped value from a message instance.
    /// </summary>
    public required Func<object, object> MessageProp { get; init; }

    /// <summary>
    /// Gets the message type the mapping applies to.
    /// </summary>
    public required Type MessageType { get; init; }

    /// <summary>
    /// Gets the process-manager property path represented as a property-name hierarchy.
    /// </summary>
    public IReadOnlyDictionary<string, Type> PropertiesHierarchy { get; init; } = new Dictionary<string, Type>(StringComparer.Ordinal);
}
