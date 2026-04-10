using RabbitMQ.Client;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Client.RabbitMQ;

public static class SslConfigurationBuilder
{
    public static SslOption BuildSslOptions(ITransportConfiguration transportSettings)
    {
        return new SslOption
        {
            Version = transportSettings.SslProtocol,
            Enabled = true,
            AcceptablePolicyErrors = transportSettings.AcceptablePolicyErrors,
            ServerName = transportSettings.ServerName,
            CertPassphrase = transportSettings.CertPassphrase,
            CertPath = transportSettings.CertPath,
            Certs = transportSettings.Certs,
            CertificateSelectionCallback = transportSettings.CertificateSelectionCallback,
            CertificateValidationCallback = transportSettings.CertificateValidationCallback
        };
    }
}
