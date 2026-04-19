using System.Security.Authentication;

namespace ServiceConnect.Persistence.MongoDb;

public sealed class MongoDbSslOptions
{
    public string? CertPath { get; set; }
    public string? CertPassphrase { get; set; }
    /// <summary>
    /// SSL/TLS protocol. Defaults to <see cref="SslProtocols.None"/>, which delegates
    /// protocol selection to the runtime so TLS 1.3 is used where available.
    /// </summary>
    public SslProtocols SslProtocol { get; set; } = SslProtocols.None;
    /// <summary>
    /// SECURITY WARNING: Setting this to <c>true</c> disables TLS certificate validation
    /// against the MongoDB endpoint, enabling man-in-the-middle attacks. Only use in
    /// development / testing with full understanding of the risks.
    /// </summary>
    /// <remarks>WARNING: When true, all MongoDB TLS certificate validation is disabled. Use only in development/testing.</remarks>
    public bool AllowInsecureTls { get; set; }
    public bool CheckCertificateRevocation { get; set; } = true;
}
