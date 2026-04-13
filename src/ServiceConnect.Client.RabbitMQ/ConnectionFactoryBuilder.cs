using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Shared <see cref="ConnectionFactory"/> builder so <see cref="Connection"/> and
/// <see cref="Producer"/> don't duplicate port/user/pass/SSL/vhost logic (A-03).
/// </summary>
internal static class ConnectionFactoryBuilder
{
    /// <summary>
    /// Builds a <see cref="ConnectionFactory"/> from the transport configuration.
    /// </summary>
    /// <param name="transport">Transport settings including SSL, credentials, and hosts.</param>
    /// <param name="heartbeatInterval">
    /// Optional heartbeat interval. Callers that read a `HeartbeatEnabled` / `HeartbeatTime`
    /// setting explicitly (Connection.cs does) pass the resolved value; callers that don't
    /// care (Producer.cs) pass <c>null</c> to accept RabbitMQ defaults.
    /// </param>
    public static ConnectionFactory Build(ITransportConfiguration transport, TimeSpan? heartbeatInterval)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var port = transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.Port, out var portVal)
            ? Convert.ToInt32(portVal)
            : AmqpTcpEndpoint.UseDefaultPort;

        var factory = new ConnectionFactory
        {
            VirtualHost = "/",
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };

        if (heartbeatInterval.HasValue)
            factory.RequestedHeartbeat = heartbeatInterval.Value;

        if (!string.IsNullOrEmpty(transport.Username))
            factory.UserName = transport.Username;

        if (!string.IsNullOrEmpty(transport.Password))
            factory.Password = transport.Password;

        if (transport.SslEnabled)
        {
            factory.Ssl = SslConfigurationBuilder.BuildSslOptions(transport);
            factory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
        }

        if (!string.IsNullOrEmpty(transport.VirtualHost))
            factory.VirtualHost = transport.VirtualHost;

        return factory;
    }
}
