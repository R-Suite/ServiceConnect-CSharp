using System.Security.Authentication;

namespace ServiceConnect.Persistence.MongoDb;

public sealed class MongoDbSslOptions
{
    public string? CertPath { get; set; }
    public string? CertPassphrase { get; set; }
    public SslProtocols SslProtocol { get; set; } = SslProtocols.Tls12;
    public bool AllowInsecureTls { get; set; }
    public bool CheckCertificateRevocation { get; set; } = true;
}
