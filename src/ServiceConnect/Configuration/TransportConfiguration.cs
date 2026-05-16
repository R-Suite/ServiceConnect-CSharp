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

    private bool _frozen;
    private string _host = "localhost";
    private string? _username;
    private string? _password;
    private string? _virtualHost;
    private int _retryDelay = DefaultRetryDelayMilliseconds;
    private int _maxRetries = DefaultMaxRetries;
    private ushort _prefetchCount = DefaultPrefetchCount;
    private int _gracefulShutdownTimeoutMilliseconds = DefaultGracefulShutdownTimeoutMilliseconds;
    private bool _sslEnabled = true;
    private SslPolicyErrors _acceptablePolicyErrors = SslPolicyErrors.None;
    private string? _serverName;
    private string? _certPath;
    private string? _certPassphrase;
    private X509CertificateCollection? _certs;
    private SslProtocols _sslProtocol = SslProtocols.None;
    private LocalCertificateSelectionCallback? _certificateSelectionCallback;
    private RemoteCertificateValidationCallback? _certificateValidationCallback;
    private readonly Dictionary<string, object> _clientSettings = [];

    /// <summary>
    /// Latches this configuration so further setter calls throw <see cref="InvalidOperationException"/>.
    /// Called by <see cref="BusConfiguration.Freeze"/> after the user's configure callback returns.
    /// </summary>
    internal void Freeze() => _frozen = true;

    private void ThrowIfFrozen([System.Runtime.CompilerServices.CallerMemberName] string? memberName = null)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"TransportConfiguration is frozen — '{memberName}' cannot be modified after AddServiceConnect has returned. " +
                "Configure all properties inside the AddServiceConnect callback.");
        }
    }

    /// <remarks>Default targets <c>localhost</c>. Override in production deployments.</remarks>
    public string Host { get => _host; set { ThrowIfFrozen(); _host = value; } }
    /// <remarks>Default unset (no authentication). Override in production deployments.</remarks>
    public string? Username { get => _username; set { ThrowIfFrozen(); _username = value; } }
    /// <remarks>Default unset (no authentication). Override in production deployments.</remarks>
    public string? Password { get => _password; set { ThrowIfFrozen(); _password = value; } }
    /// <inheritdoc />
    public string? VirtualHost { get => _virtualHost; set { ThrowIfFrozen(); _virtualHost = value; } }

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
            ThrowIfFrozen();
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RetryDelay must be non-negative (milliseconds).");
            }
            _retryDelay = value;
        }
    }

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
            ThrowIfFrozen();
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetries must be non-negative.");
            }
            _maxRetries = value;
        }
    }

    /// <inheritdoc />
    public ushort PrefetchCount { get => _prefetchCount; set { ThrowIfFrozen(); _prefetchCount = value; } }
    /// <summary>
    /// Time to wait for in-flight messages to drain during graceful shutdown, in milliseconds.
    /// </summary>
    public int GracefulShutdownTimeoutMilliseconds { get => _gracefulShutdownTimeoutMilliseconds; set { ThrowIfFrozen(); _gracefulShutdownTimeoutMilliseconds = value; } }
    /// <inheritdoc />
    public bool SslEnabled { get => _sslEnabled; set { ThrowIfFrozen(); _sslEnabled = value; } }
    /// <summary>
    /// Gets or sets the SSL policy errors that are acceptable.
    /// WARNING: Setting any value other than <see cref="SslPolicyErrors.None"/> weakens TLS security
    /// and should only be used in development/testing environments.
    /// </summary>
    public SslPolicyErrors AcceptablePolicyErrors { get => _acceptablePolicyErrors; set { ThrowIfFrozen(); _acceptablePolicyErrors = value; } }
    /// <inheritdoc />
    public string? ServerName { get => _serverName; set { ThrowIfFrozen(); _serverName = value; } }
    /// <inheritdoc />
    public string? CertPath { get => _certPath; set { ThrowIfFrozen(); _certPath = value; } }
    /// <remarks>SECURITY: This value is held in memory as plain text. Avoid logging or serializing this configuration object.</remarks>
    public string? CertPassphrase { get => _certPassphrase; set { ThrowIfFrozen(); _certPassphrase = value; } }
    /// <inheritdoc />
    public X509CertificateCollection? Certs { get => _certs; set { ThrowIfFrozen(); _certs = value; } }
    /// <summary>
    /// SSL/TLS protocol. Defaults to <see cref="SslProtocols.None"/>, which delegates
    /// protocol selection to the runtime so TLS 1.3 is used where available.
    /// </summary>
    public SslProtocols SslProtocol { get => _sslProtocol; set { ThrowIfFrozen(); _sslProtocol = value; } }
    /// <inheritdoc />
    public LocalCertificateSelectionCallback? CertificateSelectionCallback { get => _certificateSelectionCallback; set { ThrowIfFrozen(); _certificateSelectionCallback = value; } }
    /// <summary>
    /// Gets or sets a custom certificate validation callback.
    /// SECURITY WARNING: A callback that unconditionally returns <c>true</c> bypasses
    /// all TLS certificate validation, enabling man-in-the-middle attacks.
    /// Only use this in development/testing with full understanding of the risks.
    /// </summary>
    /// <remarks>WARNING: Setting this to a callback that always returns true disables all certificate validation.</remarks>
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get => _certificateValidationCallback; set { ThrowIfFrozen(); _certificateValidationCallback = value; } }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> ClientSettings => _clientSettings;

    /// <inheritdoc />
    public void SetClientSetting(string key, object value)
    {
        ThrowIfFrozen();
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _clientSettings[key] = value;
    }
}
