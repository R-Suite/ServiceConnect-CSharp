using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests.Persistence.MongoDb;

[Collection("Mongo Bson serial")]
public class MongoClientFactoryCertCallbackTests
{
    private static LocalCertificateSelectionCallback BuildCallback()
    {
        var dummyCert = CreateDummyCertificate();
        var originalLoader = MongoClientFactory.CertLoader;
        MongoClientFactory.CertLoader = (_, _) => dummyCert;

        try
        {
            var options = new MongoDbPersistenceOptions
            {
                ConnectionString = "mongodb://localhost:27017",
                DatabaseName = "test",
                Ssl = new MongoDbSslOptions
                {
                    CertPath = CreateTempPemPath(),
                    CertPassphrase = null,
                    AllowInsecureTls = true,
                },
            };

            var client = MongoClientFactory.Create(options);
            return client.Settings.SslSettings!.ClientCertificateSelectionCallback!;
        }
        finally
        {
            MongoClientFactory.CertLoader = originalLoader;
            MongoClientFactory.ClearCertificateCache();
        }
    }

    [Fact]
    public void CertificateSelectionCallback_NullCertificates_FallsBackToCertificateParam()
    {
        var callback = BuildCallback();
        var fallbackCert = CreateDummyCertificate();

        var result = callback.Invoke(this, "host", null!, fallbackCert, []);

        Assert.Same(fallbackCert, result);
    }

    [Fact]
    public void CertificateSelectionCallback_EmptyCertificates_FallsBackToCertificateParam()
    {
        var callback = BuildCallback();
        var fallbackCert = CreateDummyCertificate();
        var emptyCollection = new X509CertificateCollection();

        var result = callback.Invoke(this, "host", emptyCollection, fallbackCert, []);

        Assert.Same(fallbackCert, result);
    }

    [Fact]
    public void CertificateSelectionCallback_NonEmptyCertificates_ReturnsFirst()
    {
        var callback = BuildCallback();
        var firstCert = CreateDummyCertificate();
        var collection = new X509CertificateCollection { firstCert };

        var result = callback.Invoke(this, "host", collection, CreateDummyCertificate(), []);

        Assert.Same(firstCert, result);
    }

    private static X509Certificate2 CreateDummyCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1));
    }

    private static string CreateTempPemPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sc-test-{Guid.NewGuid()}.pem");
        File.WriteAllText(path, "dummy");
        return path;
    }
}
