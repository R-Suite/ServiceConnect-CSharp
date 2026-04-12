using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ServiceConnect.Interfaces.Configuration;

public interface ITransportConfiguration
{
    string Host { get; set; }
    string? Username { get; set; }
    string? Password { get; set; }
    string? VirtualHost { get; set; }
    int RetryDelay { get; set; }
    int MaxRetries { get; set; }
    ushort PrefetchCount { get; set; }
    bool SslEnabled { get; set; }
    SslPolicyErrors AcceptablePolicyErrors { get; set; }
    string? ServerName { get; set; }
    string? CertPath { get; set; }
    string? CertPassphrase { get; set; }
    X509CertificateCollection? Certs { get; set; }
    SslProtocols SslProtocol { get; set; }
    LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
    RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
    IReadOnlyDictionary<string, object> ClientSettings { get; }
    void SetClientSetting(string key, object value);
}
