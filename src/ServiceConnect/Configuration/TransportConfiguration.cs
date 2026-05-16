using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Interfaces.Configuration;

namespace ServiceConnect.Configuration;

/// <summary>
/// Mutable implementation of <see cref="ITransportConfiguration"/> for configuring broker connectivity and TLS.
/// </summary>
internal sealed class TransportConfiguration : ITransportConfiguration
{
    /// <summary>Default dead-letter retry delay, in milliseconds.</summary>
    public const int DefaultRetryDelayMilliseconds = 3000;
    /// <summary>Default maximum retries before a message is sent to the error queue.</summary>
    public const int DefaultMaxRetries = 3;
    /// <summary>Default RabbitMQ prefetch count per consumer.</summary>
    public const ushort DefaultPrefetchCount = 1;
    /// <summary>Default RabbitMQ graceful-shutdown drain timeout, in milliseconds.</summary>
    public const int DefaultGracefulShutdownTimeoutMilliseconds = 5000;

    /// <remarks>Default targets <c>localhost</c>. Override in production deployments.</remarks>
    public string Host { get; set; } = "localhost";
    /// <remarks>Default unset (no authentication). Override in production deployments.</remarks>
    public string? Username { get; set; }
    /// <remarks>Default unset (no authentication). Override in production deployments.</remarks>
    public string? Password { get; set; }
    /// <inheritdoc />
    public string? VirtualHost { get; set; }
    private int _retryDelay = DefaultRetryDelayMilliseconds;
    /// <summary>Dead-letter retry delay, in milliseconds. Must be non-negative.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <remarks>
    /// Negative values are rejected at the setter; a negative TTL on the broker-declared
    /// retry queue would be refused with <c>PRECONDITION_FAILED</c> when the topology is
    /// declared, masking the misconfiguration behind a transport error.
    /// </remarks>
    public int RetryDelay
    {
        get => _retryDelay;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RetryDelay must be non-negative (milliseconds).");
            }
            _retryDelay = value;
        }
    }

    private int _maxRetries = DefaultMaxRetries;
    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    /// <remarks>
    /// Negative values are rejected at the setter; a negative <c>MaxRetries</c> would
    /// route every first-failure message straight to the error exchange via the malformed
    /// <c>RetryCount</c> path (every fresh message has <c>RetryCount=0 &gt; -1</c>).
    /// </remarks>
    public int MaxRetries
    {
        get => _maxRetries;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetries must be non-negative.");
            }
            _maxRetries = value;
        }
    }
    /// <inheritdoc />
    public ushort PrefetchCount { get; set; } = DefaultPrefetchCount;
    /// <summary>
    /// Time to wait for in-flight messages to drain during graceful shutdown, in milliseconds.
    /// </summary>
    public int GracefulShutdownTimeoutMilliseconds { get; set; } = DefaultGracefulShutdownTimeoutMilliseconds;
    /// <inheritdoc />
    public bool SslEnabled { get; set; } = true;
    /// <summary>
    /// Gets or sets the SSL policy errors that are acceptable.
    /// WARNING: Setting any value other than <see cref="SslPolicyErrors.None"/> weakens TLS security
    /// and should only be used in development/testing environments.
    /// </summary>
    public SslPolicyErrors AcceptablePolicyErrors { get; set; } = SslPolicyErrors.None;
    /// <inheritdoc />
    public string? ServerName { get; set; }
    /// <inheritdoc />
    public string? CertPath { get; set; }
    /// <remarks>SECURITY: This value is held in memory as plain text. Avoid logging or serializing this configuration object.</remarks>
    public string? CertPassphrase { get; set; }
    /// <inheritdoc />
    public X509CertificateCollection? Certs { get; set; }
    /// <summary>
    /// SSL/TLS protocol. Defaults to <see cref="SslProtocols.None"/>, which delegates
    /// protocol selection to the runtime so TLS 1.3 is used where available.
    /// </summary>
    public SslProtocols SslProtocol { get; set; } = SslProtocols.None;
    /// <inheritdoc />
    public LocalCertificateSelectionCallback? CertificateSelectionCallback { get; set; }
    /// <summary>
    /// Gets or sets a custom certificate validation callback.
    /// SECURITY WARNING: A callback that unconditionally returns <c>true</c> bypasses
    /// all TLS certificate validation, enabling man-in-the-middle attacks.
    /// Only use this in development/testing with full understanding of the risks.
    /// </summary>
    /// <remarks>WARNING: Setting this to a callback that always returns true disables all certificate validation.</remarks>
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; set; }
    private readonly Dictionary<string, object> _clientSettings = [];
    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> ClientSettings => _clientSettings;

    /// <inheritdoc />
    public void SetClientSetting(string key, object value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _clientSettings[key] = value;
    }
}
