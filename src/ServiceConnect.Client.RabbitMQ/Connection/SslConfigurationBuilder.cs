using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

/// <summary>
/// Builds RabbitMQ SSL options from ServiceConnect transport configuration.
/// </summary>
public static class SslConfigurationBuilder
{
    /// <summary>
    /// Creates an <see cref="SslOption"/> instance for RabbitMQ connections.
    /// </summary>
    /// <param name="transportSettings">The transport settings that contain SSL-related values.</param>
    /// <returns>A configured <see cref="SslOption"/> instance with SSL enabled.</returns>
    public static SslOption BuildSslOptions(ITransportConfiguration transportSettings)
    {
        if (string.IsNullOrWhiteSpace(transportSettings.ServerName))
        {
            throw new ArgumentException("ServerName is required when SSL is enabled. Configure ITransportConfiguration.ServerName.", nameof(transportSettings));
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
