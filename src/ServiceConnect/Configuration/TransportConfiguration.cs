using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class TransportConfiguration : ITransportConfiguration
{
    public string Host { get; set; } = "localhost";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? VirtualHost { get; set; }
    public int RetryDelay { get; set; } = 3000;
    public int MaxRetries { get; set; } = 3;
    public ushort PrefetchCount { get; set; } = 1;
    public bool SslEnabled { get; set; }
    /// <summary>
    /// Gets or sets the SSL policy errors that are acceptable.
    /// WARNING: Setting any value other than <see cref="SslPolicyErrors.None"/> weakens TLS security
    /// and should only be used in development/testing environments.
    /// </summary>
    public SslPolicyErrors AcceptablePolicyErrors { get; set; } = SslPolicyErrors.None;
    public string? ServerName { get; set; }
    public string? CertPath { get; set; }
    public string? CertPassphrase { get; set; }
    public X509CertificateCollection? Certs { get; set; }
    public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls12;
    public LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
    /// <summary>
    /// Gets or sets a custom certificate validation callback.
    /// SECURITY WARNING: A callback that unconditionally returns <c>true</c> bypasses
    /// all TLS certificate validation, enabling man-in-the-middle attacks.
    /// Only use this in development/testing with full understanding of the risks.
    /// </summary>
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
    public IDictionary<string, object> ClientSettings { get; set; } = new Dictionary<string, object>();
}
