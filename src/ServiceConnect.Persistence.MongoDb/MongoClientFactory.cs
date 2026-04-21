using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Driver;

namespace ServiceConnect.Persistence.MongoDb;

/// <summary>
/// Factory that builds MongoClient with or without SSL based on configuration options.
/// </summary>
public static class MongoClientFactory
{
    // Cache loaded certificates so repeated Create() calls never duplicate the native handle.
    // The cert's lifetime is then bounded by the process (or explicit ClearCertificateCache()
    // in tests) — aligning with the MongoClient singleton that holds a reference to it.
    private static readonly ConcurrentDictionary<string, X509Certificate2> _certCache = new();

    /// <summary>
    /// Creates a <see cref="MongoClient"/> using the configured connection and SSL options.
    /// </summary>
    /// <param name="options">The MongoDB persistence options to apply.</param>
    /// <returns>A configured <see cref="MongoClient"/> instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The connection string is missing.</exception>
    public static MongoClient Create(MongoDbPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "MongoDbPersistenceOptions.ConnectionString is required. Configure via IOptions<MongoDbPersistenceOptions> or builder.");

        // Must register Guid serializer BEFORE any MongoClient reads/writes so data is
        // encoded as UUID subtype 4 (Standard) from the start. Direct callers of this
        // factory bypass UseMongoDbPersistence, so the registration guard lives here too
        // from the start.
        MongoDbPersistenceExtensions.EnsureGuidSerializerRegistered();

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

        // Protocol and revocation settings must apply to every TLS connection, not only
        // when a client cert is configured — otherwise certless TLS users silently fall
        // back to driver defaults for both.
        // AllowInsecureTls and CheckCertificateRevocation=true are incompatible (driver
        // rejects the combination), so revocation check is forced off when insecure TLS
        // is explicitly requested.
        var ssl = new SslSettings
        {
            CheckCertificateRevocation = !sslOptions.AllowInsecureTls && sslOptions.CheckCertificateRevocation,
            EnabledSslProtocols = sslOptions.SslProtocol
        };

        if (!string.IsNullOrEmpty(sslOptions.CertPath))
        {
            var cert = GetOrLoadCertificate(sslOptions.CertPath, sslOptions.CertPassphrase);
            ssl.ClientCertificates = new[] { cert };
            ssl.ClientCertificateSelectionCallback = (sender, host, certificates, certificate, issuers) => certificates[0];
        }

        settings.SslSettings = ssl;

        return new MongoClient(settings);
    }

    private static X509Certificate2 GetOrLoadCertificate(string path, string? passphrase)
    {
        // Key must include passphrase changes so a rotated cert is picked up even when the
        // file path stays the same.
        var cacheKey = path + "\0" + (passphrase ?? string.Empty);
        return _certCache.GetOrAdd(cacheKey, _ => LoadCertificate(path, passphrase));
    }

    private static X509Certificate2 LoadCertificate(string path, string? passphrase)
    {
#if NET9_0_OR_GREATER
        return string.IsNullOrEmpty(passphrase)
            ? X509CertificateLoader.LoadCertificateFromFile(path)
            : X509CertificateLoader.LoadPkcs12FromFile(path, passphrase);
#else
        return string.IsNullOrEmpty(passphrase)
            ? new X509Certificate2(path)
            : new X509Certificate2(path, passphrase);
#endif
    }

    /// <summary>
    /// Clears the internal certificate cache. Intended for test teardown; do not call in
    /// production — live <see cref="MongoClient"/> instances still hold references to the
    /// removed certificates, and clearing only releases the factory's own reference.
    /// </summary>
    internal static void ClearCertificateCache()
    {
        foreach (var cert in _certCache.Values)
        {
            cert.Dispose();
        }
        _certCache.Clear();
    }
}
