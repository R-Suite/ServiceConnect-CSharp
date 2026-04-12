using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public static class SslConfigurationBuilder
{
    public static SslOption BuildSslOptions(ITransportConfiguration transportSettings)
    {
        if (string.IsNullOrWhiteSpace(transportSettings.ServerName))
            throw new ArgumentException("ServerName is required when SSL is enabled. Configure ITransportConfiguration.ServerName.", nameof(transportSettings));

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
            sslOption.CertificateValidationCallback = transportSettings.CertificateValidationCallback;

        return sslOption;
    }
}
