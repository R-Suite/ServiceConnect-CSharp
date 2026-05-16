using Microsoft.Extensions.Logging;

namespace ServiceConnect;

/// <summary>
/// Source-generated logger entries emitted by the core ServiceConnect package.
/// </summary>
internal static partial class ServiceConnectLog
{
    /// <summary>
    /// Stable event id for the plaintext-on-non-loopback warning emitted at host startup.
    /// </summary>
    public const int PlaintextOnNonLoopbackHostEventId = 100;

    [LoggerMessage(
        EventId = PlaintextOnNonLoopbackHostEventId,
        EventName = "PlaintextOnNonLoopbackHost",
        Level = LogLevel.Warning,
        Message = "ServiceConnect transport is configured for plaintext (SslEnabled=false) against non-loopback host '{Host}'. Production deployments should use TLS; set SslEnabled=true (the default) and configure certificates. To suppress this warning set SuppressPlaintextWarning=true on the transport configuration.")]
    public static partial void PlaintextOnNonLoopbackHost(ILogger logger, string host);
}
