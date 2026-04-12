using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// Factory that builds MongoClient with or without SSL based on configuration options.
/// </summary>
public static class MongoClientFactory
{
    public static MongoClient Create(MongoDbPersistenceOptions options)
    {
        if (options.Ssl is null)
        {
            return new MongoClient(options.ConnectionString);
        }

        return CreateSslClient(options);
    }

    private static MongoClient CreateSslClient(MongoDbPersistenceOptions options)
    {
        var sslOptions = options.Ssl!;
        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);

        settings.UseTls = true;
        settings.AllowInsecureTls = sslOptions.AllowInsecureTls;

        if (!string.IsNullOrEmpty(sslOptions.CertPath))
        {
#if NET9_0_OR_GREATER
            var cert = string.IsNullOrEmpty(sslOptions.CertPassphrase)
                ? X509CertificateLoader.LoadCertificateFromFile(sslOptions.CertPath)
                : X509CertificateLoader.LoadPkcs12FromFile(sslOptions.CertPath, sslOptions.CertPassphrase);
#else
            var cert = string.IsNullOrEmpty(sslOptions.CertPassphrase)
                ? new X509Certificate2(sslOptions.CertPath)
                : new X509Certificate2(sslOptions.CertPath, sslOptions.CertPassphrase);
#endif

            settings.SslSettings = new SslSettings
            {
                ClientCertificates = new[] { cert },
                ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0],
                CheckCertificateRevocation = sslOptions.CheckCertificateRevocation,
                EnabledSslProtocols = sslOptions.SslProtocol
            };
        }

        return new MongoClient(settings);
    }
}
