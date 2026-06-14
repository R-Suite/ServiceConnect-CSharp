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

    /// <summary>
    /// Gets the broker host name or IP address. Used to populate the <c>server.address</c>
    /// OTel semantic-convention attribute on producer and consumer spans.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <see cref="string.Empty"/>; implementations that have
    /// access to transport configuration should return the first configured host.
    /// </remarks>
    string ServerAddress => string.Empty;

    /// <summary>
    /// Gets the broker TCP port. Used to populate the <c>server.port</c> OTel
    /// semantic-convention attribute on producer and consumer spans.
    /// </summary>
    /// <remarks>
    /// Default implementation returns <c>0</c>; implementations that have access to transport
    /// configuration should return the configured port.
    /// </remarks>
    int ServerPort => 0;
}
