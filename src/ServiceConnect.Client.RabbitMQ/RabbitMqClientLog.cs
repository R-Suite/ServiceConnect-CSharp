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

    [LoggerMessage(
        EventId = PlaintextOnNonLoopbackHostEventId,
        EventName = "PlaintextOnNonLoopbackHost",
        Level = LogLevel.Warning,
        Message = "ServiceConnect transport is configured for plaintext (SslEnabled=false) against a non-loopback host '{Host}'. Production deployments should use TLS; set SslEnabled=true (the v8 default) and configure certificates. To suppress this warning in environments where plaintext is intentional, raise the ServiceConnect.Client.RabbitMQ category to Error.")]
    public static partial void PlaintextOnNonLoopbackHost(ILogger logger, string host);
}
