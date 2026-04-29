using System.Globalization;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Shared <see cref="ConnectionFactory"/> builder so <see cref="Connection"/> and
/// <see cref="Producer"/> don't duplicate port/user/pass/SSL/vhost logic.
/// </summary>
internal static class ConnectionFactoryBuilder
{
    /// <summary>
    /// Default heartbeat interval applied when the caller hasn't configured one.
    /// Shared between producer and consumer so both paths use the same fallback.
    /// </summary>
    private static readonly TimeSpan DefaultHeartbeat = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Builds a <see cref="ConnectionFactory"/> from the transport configuration.
    /// Heartbeat is resolved from <see cref="RabbitMQSettingKeys.HeartbeatEnabled"/> and
    /// <see cref="RabbitMQSettingKeys.HeartbeatTime"/> so producer and consumer code paths
    /// honour the same configured values.
    /// </summary>
    /// <param name="transport">Transport settings including SSL, credentials, and hosts.</param>
    public static ConnectionFactory Build(ITransportConfiguration transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var explicitPortConfigured = transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.Port, out var portVal);
        var port = explicitPortConfigured
            ? ConvertSettingToInt32(RabbitMQSettingKeys.Port, portVal)
            : AmqpTcpEndpoint.UseDefaultPort;

        var factory = new ConnectionFactory
        {
            VirtualHost = "/",
            Port = port,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            RequestedHeartbeat = ResolveHeartbeat(transport),
        };

        if (!string.IsNullOrEmpty(transport.Username))
        {
            factory.UserName = transport.Username;
        }

        if (!string.IsNullOrEmpty(transport.Password))
        {
            factory.Password = transport.Password;
        }

        if (transport.SslEnabled)
        {
            factory.Ssl = SslConfigurationBuilder.BuildSslOptions(transport);
            // Only fall back to the default AMQPS port when the user didn't supply one.
            // Respecting an explicit port lets TLS deployments on non-default ports connect.
            if (!explicitPortConfigured)
            {
                factory.Port = AmqpTcpEndpoint.DefaultAmqpSslPort;
            }
        }

        if (!string.IsNullOrEmpty(transport.VirtualHost))
        {
            factory.VirtualHost = transport.VirtualHost;
        }

        return factory;
    }

    // Wraps Convert.ToInt32 so that any conversion failure carries the setting key and
    // the offending value, making misconfiguration far easier to diagnose at runtime.
    private static int ConvertSettingToInt32(string key, object? value)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Setting '{key}' must be convertible to Int32; got value '{value}' of type '{value?.GetType().FullName ?? "<null>"}'.",
                ex);
        }
    }

    private static TimeSpan ResolveHeartbeat(ITransportConfiguration transport)
    {
        var settings = transport.ClientSettings;

        // Explicit opt-out disables heartbeats (TimeSpan.Zero == "never send one").
        if (settings.TryGetValue(RabbitMQSettingKeys.HeartbeatEnabled, out var enabledRaw)
            && enabledRaw is bool enabled && !enabled)
        {
            return TimeSpan.Zero;
        }

        if (settings.TryGetValue(RabbitMQSettingKeys.HeartbeatTime, out var timeRaw))
        {
            return TimeSpan.FromSeconds(ConvertSettingToInt32(RabbitMQSettingKeys.HeartbeatTime, timeRaw));
        }

        return DefaultHeartbeat;
    }
}
