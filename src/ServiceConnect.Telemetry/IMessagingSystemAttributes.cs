namespace ServiceConnect.Telemetry;

/// <summary>
/// Supplies semantic-convention values that identify the messaging system and transport protocol.
/// </summary>
public interface IMessagingSystemAttributes
{
    /// <summary>
    /// Gets the OpenTelemetry messaging-system identifier.
    /// </summary>
    string MessagingSystem { get; }

    /// <summary>
    /// Gets the network protocol name used by the messaging system.
    /// </summary>
    string ProtocolName { get; }
}
