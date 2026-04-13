using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

public sealed class TransportConfiguration : ITransportConfiguration
{
    /// <summary>Default dead-letter retry delay, in milliseconds.</summary>
    public const int DefaultRetryDelayMilliseconds = 3000;
    /// <summary>Default maximum retries before a message is sent to the error queue.</summary>
    public const int DefaultMaxRetries = 3;
    /// <summary>Default RabbitMQ prefetch count per consumer.</summary>
    public const ushort DefaultPrefetchCount = 1;
    /// <summary>Default RabbitMQ graceful-shutdown drain timeout, in milliseconds.</summary>
    public const int DefaultGracefulShutdownTimeoutMilliseconds = 5000;

    public string Host { get; set; } = "localhost";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? VirtualHost { get; set; }
    /// <summary>Dead-letter retry delay, in milliseconds. Must be non-negative.</summary>
    public int RetryDelay { get; set; } = DefaultRetryDelayMilliseconds;
    public int MaxRetries { get; set; } = DefaultMaxRetries;
    public ushort PrefetchCount { get; set; } = DefaultPrefetchCount;
    /// <summary>
    /// Time to wait for in-flight messages to drain during graceful shutdown, in milliseconds. (C-09)
    /// </summary>
    public int GracefulShutdownTimeoutMilliseconds { get; set; } = DefaultGracefulShutdownTimeoutMilliseconds;
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
    private readonly Dictionary<string, object> _clientSettings = new();
    public IReadOnlyDictionary<string, object> ClientSettings => _clientSettings;

    public void SetClientSetting(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _clientSettings[key] = value;
    }
}
