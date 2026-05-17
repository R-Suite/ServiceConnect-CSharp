using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Telemetry;

/// <summary>
/// RabbitMQ-specific semantic-convention values used by telemetry spans.
/// </summary>
public sealed class RabbitMqMessagingSystemAttributes : IMessagingSystemAttributes
{
    private readonly string _serverAddress;
    private readonly int _serverPort;

    /// <summary>
    /// Initialises an instance sourcing the broker endpoint from <paramref name="transport"/>.
    /// </summary>
    /// <param name="transport">
    /// Transport configuration from which <c>server.address</c> and <c>server.port</c> are derived.
    /// </param>
    public RabbitMqMessagingSystemAttributes(ITransportConfiguration transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        // Host may be a comma-separated cluster list (e.g. "rabbit1,rabbit2"). The transport
        // splits on ',' only; mirror that here so server.address reflects what the connection
        // factory will actually dial. A semicolon in Host is part of the literal hostname.
        var host = transport.Host ?? string.Empty;
        var idx = host.IndexOf(',');
        var first = idx >= 0 ? host[..idx] : host;
        _serverAddress = first.Trim();

        // Port lives in ClientSettings because ITransportConfiguration does not expose it
        // directly. Fall back to 0 when unconfigured (the factory defaults to the AMQP
        // well-known port; 0 is the "not set" sentinel for the server.port span attribute).
        _serverPort = transport.ClientSettings.TryGetValue("Port", out var portVal)
            ? ConvertPort(portVal)
            : 0;
    }

    // Parameterless constructor preserved for tests that don't care about broker endpoints.
    // Returns empty-string / 0 defaults from the interface DIM.

    /// <summary>
    /// Initialises an instance with no broker endpoint information.
    /// </summary>
    /// <remarks>
    /// Provided for test scenarios that construct attributes without a transport configuration.
    /// <c>server.address</c> and <c>server.port</c> will be empty / zero.
    /// </remarks>
    public RabbitMqMessagingSystemAttributes()
    {
        _serverAddress = string.Empty;
        _serverPort = 0;
    }

    /// <inheritdoc />
    public string MessagingSystem => "rabbitmq";

    /// <inheritdoc />
    public string ProtocolName => "amqp";

    /// <inheritdoc />
    public string ServerAddress => _serverAddress;

    /// <inheritdoc />
    public int ServerPort => _serverPort;

    private static int ConvertPort(object? value)
    {
        try
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            // Ignore malformed port settings; 0 signals "not available" to the span builder.
            return 0;
        }
    }
}
