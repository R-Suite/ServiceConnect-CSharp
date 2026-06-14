namespace ServiceConnect.Interfaces;

/// <summary>
/// Describes the message type handled by a registered handler type, and which handler
/// interface the reference was produced for.
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

    /// <summary>
    /// Gets the handler interface kind this reference was produced for.
    /// A class that implements both <see cref="IMessageHandler{T}"/> and
    /// <see cref="IProcessHandler{TData,T}"/> for the same message type produces
    /// two separate references, one per kind.
    /// </summary>
    public HandlerInterfaceKind InterfaceKind { get; init; } = HandlerInterfaceKind.MessageHandler;
}
