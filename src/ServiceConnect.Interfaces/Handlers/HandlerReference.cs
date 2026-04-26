namespace ServiceConnect.Interfaces;

/// <summary>
/// Describes the message type handled by a registered handler type.
/// </summary>
public sealed class HandlerReference
{
    /// <summary>
    /// Gets the message type handled by the registration.
    /// </summary>
    public required Type MessageType { get; init; }

    /// <summary>
    /// Gets the concrete handler type.
    /// </summary>
    public required Type HandlerType { get; init; }
}
