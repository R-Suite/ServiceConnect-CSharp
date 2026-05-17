using System.Net.Security;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Builds RabbitMQ SSL options from ServiceConnect transport configuration.
/// </summary>
internal static class SslConfigurationBuilder
{
    /// <summary>
    /// Creates an <see cref="SslOption"/> instance for RabbitMQ connections.
    /// </summary>
    /// <param name="transportSettings">The transport settings that contain SSL-related values.</param>
    /// <param name="logger">Logger used to surface TLS misconfiguration at startup.</param>
    /// <returns>A configured <see cref="SslOption"/> instance with SSL enabled.</returns>
    public static SslOption BuildSslOptions(ITransportConfiguration transportSettings, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(transportSettings.ServerName))
        {
            throw new ArgumentException("ServerName is required when SSL is enabled. Configure ITransportConfiguration.ServerName.", nameof(transportSettings));
        }

        if (transportSettings.AcceptablePolicyErrors != SslPolicyErrors.None)
        {
            logger.LogWarning(
                "AcceptablePolicyErrors is set to {Errors}, which weakens TLS certificate validation. " +
                "Set to SslPolicyErrors.None in production.",
                transportSettings.AcceptablePolicyErrors);
        }

        if (transportSettings.CertificateValidationCallback != null)
        {
            logger.LogWarning(
                "A custom CertificateValidationCallback is configured. A callback that returns true " +
                "unconditionally disables all TLS certificate validation.");
        }

        var sslOption = new SslOption
        {
            Enabled = true,
            ServerName = transportSettings.ServerName!,
            CertPath = transportSettings.CertPath ?? string.Empty,
            AcceptablePolicyErrors = transportSettings.AcceptablePolicyErrors,
            Certs = transportSettings.Certs,
            Version = transportSettings.SslProtocol,
            CertPassphrase = transportSettings.CertPassphrase,
            CertificateSelectionCallback = transportSettings.CertificateSelectionCallback
        };

        if (transportSettings.CertificateValidationCallback != null)
        {
            sslOption.CertificateValidationCallback = transportSettings.CertificateValidationCallback;
        }

        return sslOption;
    }
}
