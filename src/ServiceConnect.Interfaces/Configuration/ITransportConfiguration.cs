using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ServiceConnect.Interfaces.Configuration;

/// <summary>
/// Configures transport connectivity, retries, and TLS behavior.
/// </summary>
public interface ITransportConfiguration
{
    /// <summary>
    /// Gets or sets the transport host name.
    /// </summary>
    string Host { get; set; }

    /// <summary>
    /// Gets or sets the transport username.
    /// </summary>
    string? Username { get; set; }

    /// <summary>
    /// Gets or sets the transport password.
    /// </summary>
    string? Password { get; set; }

    /// <summary>
    /// Gets or sets the virtual host or namespace used by the broker.
    /// </summary>
    string? VirtualHost { get; set; }
    /// <summary>Dead-letter retry delay, in milliseconds.</summary>
    int RetryDelay { get; set; }

    /// <summary>
    /// Gets or sets the maximum retry attempts before the message is treated as terminally failed.
    /// </summary>
    int MaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the consumer prefetch count.
    /// </summary>
    ushort PrefetchCount { get; set; }
    /// <summary>
    /// Time to wait for in-flight messages to drain during graceful shutdown, in milliseconds.
    /// </summary>
    int GracefulShutdownTimeoutMilliseconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether TLS is enabled. Defaults to <see langword="true"/>:
    /// the framework connects to the broker over TLS on port 5671 unless overridden.
    /// </summary>
    /// <remarks>
    /// To connect to a plaintext broker (e.g. a local RabbitMQ in Docker without TLS configured),
    /// set this to <see langword="false"/>; the framework logs a <c>Warning</c> when TLS is
    /// disabled against a non-loopback host unless <see cref="SuppressPlaintextWarning"/> is
    /// set to <see langword="true"/>.
    /// </remarks>
    bool SslEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plaintext-against-non-loopback-host warning
    /// is suppressed. Set to <see langword="true"/> in environments where plaintext is intentional
    /// (e.g. Docker Compose service names such as <c>"rabbitmq"</c> that resolve to an internal
    /// network address but are not loopback). Defaults to <see langword="false"/>.
    /// </summary>
    bool SuppressPlaintextWarning { get; set; }

    /// <summary>
    /// Gets or sets the TLS policy errors that are tolerated during remote certificate validation.
    /// </summary>
    SslPolicyErrors AcceptablePolicyErrors { get; set; }

    /// <summary>
    /// Gets or sets the expected remote server name for TLS validation.
    /// </summary>
    string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets the client certificate file path.
    /// </summary>
    string? CertPath { get; set; }

    /// <summary>
    /// Gets or sets the passphrase used to open the client certificate file.
    /// </summary>
    string? CertPassphrase { get; set; }

    /// <summary>
    /// Gets or sets the in-memory client certificates to present to the broker.
    /// </summary>
    X509CertificateCollection? Certs { get; set; }

    /// <summary>
    /// Gets or sets the TLS protocol selection.
    /// </summary>
    SslProtocols SslProtocol { get; set; }

    /// <summary>
    /// Gets or sets the callback used to choose a local client certificate.
    /// </summary>
    LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }

    /// <summary>
    /// Gets or sets the callback used to validate the remote certificate.
    /// </summary>
    RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }

    /// <summary>
    /// Gets the provider-specific client settings.
    /// </summary>
    IReadOnlyDictionary<string, object> ClientSettings { get; }

    /// <summary>
    /// Stores a provider-specific client setting.
    /// </summary>
    /// <param name="key">The setting key.</param>
    /// <param name="value">The setting value.</param>
    void SetClientSetting(string key, object value);
}
