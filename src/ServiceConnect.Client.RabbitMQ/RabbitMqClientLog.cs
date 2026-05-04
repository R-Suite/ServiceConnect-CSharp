using Microsoft.Extensions.Logging;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Source-generated logger entries emitted by the RabbitMQ client package.
/// </summary>
internal static partial class RabbitMqClientLog
{
    /// <summary>
    /// Stable event id for the plaintext-on-non-loopback warning emitted at connection setup.
    /// </summary>
    public const int PlaintextOnNonLoopbackHostEventId = 1;

    public const int ConnectionOpenedEventId = 2;
    public const int ProducerConnectionOpenedEventId = 3;
    public const int ConnectionRecoveredEventId = 4;
    public const int ConnectionLostEventId = 5;
    public const int AckFailedEventId = 6;
    public const int NackFailedEventId = 7;

    [LoggerMessage(
        EventId = PlaintextOnNonLoopbackHostEventId,
        EventName = "PlaintextOnNonLoopbackHost",
        Level = LogLevel.Warning,
        Message = "ServiceConnect transport is configured for plaintext (SslEnabled=false) against a non-loopback host '{Host}'. Production deployments should use TLS; set SslEnabled=true (the v8 default) and configure certificates. To suppress this warning in environments where plaintext is intentional, raise the ServiceConnect.Client.RabbitMQ category to Error.")]
    public static partial void PlaintextOnNonLoopbackHost(ILogger logger, string host);

    [LoggerMessage(
        EventId = ConnectionOpenedEventId,
        EventName = "ConnectionOpened",
        Level = LogLevel.Information,
        Message = "ServiceConnect connection opened to {Host}:{Port} (vhost='{VirtualHost}', name='{ConnectionName}').")]
    public static partial void ConnectionOpened(ILogger logger, string host, int port, string virtualHost, string connectionName);

    [LoggerMessage(
        EventId = ProducerConnectionOpenedEventId,
        EventName = "ProducerConnectionOpened",
        Level = LogLevel.Information,
        Message = "ServiceConnect producer connection opened to {Host}:{Port} (vhost='{VirtualHost}', name='{ConnectionName}').")]
    public static partial void ProducerConnectionOpened(ILogger logger, string host, int port, string virtualHost, string connectionName);

    [LoggerMessage(
        EventId = ConnectionRecoveredEventId,
        EventName = "ConnectionRecovered",
        Level = LogLevel.Information,
        Message = "ServiceConnect connection recovered to {Host}:{Port} (name='{ConnectionName}').")]
    public static partial void ConnectionRecovered(ILogger logger, string host, int port, string connectionName);

    // Connection-lost stays at Information level: broker-initiated shutdowns happen for normal
    // reasons (rolling restarts, cluster maintenance) and don't warrant a Warning page. The
    // Initiator and Reason fields let log readers correlate with the broker's own logs when
    // an investigation is needed.
    [LoggerMessage(
        EventId = ConnectionLostEventId,
        EventName = "ConnectionLost",
        Level = LogLevel.Information,
        Message = "ServiceConnect connection lost to {Host}:{Port} (name='{ConnectionName}', initiator={Initiator}, reason={Reason}).")]
    public static partial void ConnectionLost(ILogger logger, string host, int port, string connectionName, string initiator, string reason);

    [LoggerMessage(
        EventId = AckFailedEventId,
        EventName = "AckFailed",
        Level = LogLevel.Warning,
        Message = "Failed to ack message {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}.")]
    public static partial void AckFailed(ILogger logger, Exception exception, string messageId, ulong deliveryTag, string queue);

    [LoggerMessage(
        EventId = NackFailedEventId,
        EventName = "NackFailed",
        Level = LogLevel.Warning,
        Message = "Failed to nack message {MessageId} (DeliveryTag {DeliveryTag}) on queue {Queue}.")]
    public static partial void NackFailed(ILogger logger, Exception exception, string messageId, ulong deliveryTag, string queue);
}
