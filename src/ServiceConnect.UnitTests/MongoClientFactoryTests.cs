using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

public class MongoClientFactoryTests : IDisposable
{
    private const string Passphrase = "testpass";

    private readonly string _certPath;   // DER-encoded public cert (no private key)
    private readonly string _pfxPath;    // PKCS#12 with private key, password-protected

    public MongoClientFactoryTests()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=serviceconnect-unittest",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        _certPath = Path.Combine(Path.GetTempPath(), $"sc-mongoclientfactory-{Guid.NewGuid():N}.cer");
        _pfxPath = Path.Combine(Path.GetTempPath(), $"sc-mongoclientfactory-{Guid.NewGuid():N}.pfx");

        File.WriteAllBytes(_certPath, cert.Export(X509ContentType.Cert));
        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx, Passphrase));
    }

    public void Dispose()
    {
        if (File.Exists(_certPath)) File.Delete(_certPath);
        if (File.Exists(_pfxPath)) File.Delete(_pfxPath);
    }

    [Fact]
    public void Create_ReturnsClient_WithoutSsl_WhenSslNull()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = null
        };

        var client = MongoClientFactory.Create(options);

        Assert.False(client.Settings.UseTls);
    }

    [Fact]
    public void Create_EnablesUseTls_WhenSslConfigured()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions()
        };

        var client = MongoClientFactory.Create(options);

        Assert.True(client.Settings.UseTls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_PropagatesAllowInsecureTls_FromOptions(bool allowInsecure)
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions { AllowInsecureTls = allowInsecure }
        };

        var client = MongoClientFactory.Create(options);

        Assert.Equal(allowInsecure, client.Settings.AllowInsecureTls);
    }

    [Fact]
    public void Create_WithoutCertPath_LeavesClientCertificatesUnset()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions() // CertPath is null
        };

        var client = MongoClientFactory.Create(options);

        // The factory doesn't touch SslSettings when CertPath is empty — the driver may
        // still auto-initialize SslSettings with defaults when UseTls is enabled, but the
        // factory's responsibility is to avoid injecting a client certificate.
        Assert.Null(client.Settings.SslSettings?.ClientCertificates);
    }

    [Fact]
    public void Create_WithCertPath_NoPassphrase_LoadsPublicCert_AndSetsCheckCertificateRevocation()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions
            {
                CertPath = _certPath,
                CheckCertificateRevocation = false
            }
        };

        var client = MongoClientFactory.Create(options);

        Assert.NotNull(client.Settings.SslSettings);
        var certs = client.Settings.SslSettings!.ClientCertificates!.Cast<X509Certificate>().ToList();
        Assert.Single(certs);
        Assert.False(client.Settings.SslSettings.CheckCertificateRevocation);
    }

    [Fact]
    public void Create_WithCertPath_AndPassphrase_LoadsPasswordProtectedCert()
    {
        var options = new MongoDbPersistenceOptions
        {
            ConnectionString = "mongodb://localhost:27017",
            Ssl = new MongoDbSslOptions
            {
                CertPath = _pfxPath,
                CertPassphrase = Passphrase,
                CheckCertificateRevocation = true
            }
        };

        var client = MongoClientFactory.Create(options);

        Assert.NotNull(client.Settings.SslSettings);
        var certs = client.Settings.SslSettings!.ClientCertificates!.Cast<X509Certificate>().ToList();
        Assert.Single(certs);
        Assert.True(client.Settings.SslSettings.CheckCertificateRevocation);
    }
}
