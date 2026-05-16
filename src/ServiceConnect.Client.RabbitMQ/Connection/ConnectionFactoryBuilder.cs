using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// </summary>
    /// <param name="transport">Transport settings including SSL, credentials, and hosts.</param>
    /// <param name="logger">Optional logger for adapter-level diagnostics.</param>
    public static ConnectionFactory Build(ITransportConfiguration transport, ILogger? logger = null)
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
            RequestedHeartbeat = ResolveHeartbeat(transport, logger ?? NullLogger.Instance),
        };

        // Apply NetworkRecoveryInterval only when explicitly configured. The unset path leaves
        // RabbitMQ.Client's own default in place, so a future client release that adjusts the
        // default isn't silently overridden by an opinionated value here. Throw on bad type to
        // surface misconfiguration loudly, matching the convention in ConvertSettingToInt32.
        if (transport.ClientSettings.TryGetValue(RabbitMQSettingKeys.NetworkRecoveryInterval, out var recoveryRaw))
        {
            if (recoveryRaw is not TimeSpan recoveryInterval)
            {
                throw new InvalidOperationException(
                    $"Setting '{RabbitMQSettingKeys.NetworkRecoveryInterval}' must be a TimeSpan; got value '{recoveryRaw}' of type '{recoveryRaw?.GetType().FullName ?? "<null>"}'.");
            }
            factory.NetworkRecoveryInterval = recoveryInterval;
        }

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

    private static TimeSpan ResolveHeartbeat(ITransportConfiguration transport, ILogger logger)
    {
        var settings = transport.ClientSettings;

        // Explicit opt-out disables heartbeats (TimeSpan.Zero == "never send one"). Without
        // heartbeats, dead-peer detection falls to TCP keepalive (Linux default ~2 hours of
        // idle), so the broker holds channel state for stale connections for hours and the
        // client never observes ConnectionShutdownAsync. Surface the consequence loudly so
        // the operator can see they've opted into it; the xmldoc on RabbitMQSettingKeys
        // .HeartbeatEnabled documents the same caveat for static analysis.
        if (settings.TryGetValue(RabbitMQSettingKeys.HeartbeatEnabled, out var enabledRaw)
            && enabledRaw is bool enabled && !enabled)
        {
            logger.LogWarning(
                "AMQP heartbeats are explicitly disabled ({Setting}=false). Dead-peer detection now relies solely on TCP keepalive (Linux default ~2h idle); a crashed or firewall-isolated client will not be observed for hours, and stale connections hold broker-side channel state. Production deployments should leave heartbeats enabled and tune {HeartbeatTime} instead.",
                RabbitMQSettingKeys.HeartbeatEnabled,
                RabbitMQSettingKeys.HeartbeatTime);
            return TimeSpan.Zero;
        }

        if (settings.TryGetValue(RabbitMQSettingKeys.HeartbeatTime, out var timeRaw))
        {
            return TimeSpan.FromSeconds(ConvertSettingToInt32(RabbitMQSettingKeys.HeartbeatTime, timeRaw));
        }

        return DefaultHeartbeat;
    }
}
