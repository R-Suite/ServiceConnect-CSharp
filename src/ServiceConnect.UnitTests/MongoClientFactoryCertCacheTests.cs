using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using ServiceConnect.Persistence.MongoDb;
using Xunit;

namespace ServiceConnect.UnitTests;

[Collection("MongoClientFactory")]
public class MongoClientFactoryCertCacheTests
{
    [Fact]
    public async Task GetOrLoadCertificate_ConcurrentFirstLoad_InvokesLoaderOnce()
    {
        // Arrange: build a single in-memory self-signed cert that the substitute loader
        // returns. We don't dispose this — Lazy hands it out and the cache owns it.
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=cert-cache-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sharedCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        MongoClientFactory.ClearCertificateCache();
        var originalLoader = MongoClientFactory.CertLoader;

        var loadCount = 0;
        MongoClientFactory.CertLoader = (path, passphrase) =>
        {
            Interlocked.Increment(ref loadCount);
            // Simulate a non-trivial load so threads actually contend on the factory.
            Thread.Sleep(50);
            return sharedCert;
        };

        try
        {
            // Act: race 32 threads through GetOrLoadCertificate (private) via reflection,
            // gated on a manual reset event so they all enter the GetOrAdd at roughly the
            // same instant.
            var method = typeof(MongoClientFactory).GetMethod(
                "GetOrLoadCertificate",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            using var gate = new ManualResetEventSlim(false);
            const int threadCount = 32;
            var tasks = new Task[threadCount];

            for (var i = 0; i < threadCount; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    gate.Wait();
                    method!.Invoke(null, ["/fake/path.pfx", "pw"]);
                });
            }

            gate.Set();
            await Task.WhenAll(tasks);

            // Assert: Lazy<T>(ExecutionAndPublication) guarantees the loader fires exactly
            // once even though many threads enter GetOrAdd concurrently. Pre-fix this would
            // typically be > 1.
            Assert.Equal(1, loadCount);
        }
        finally
        {
            MongoClientFactory.CertLoader = originalLoader;
            MongoClientFactory.ClearCertificateCache();
        }
    }

    [Fact]
    public void GetOrLoadCertificate_LoaderThrows_DoesNotPoisonCache()
    {
        MongoClientFactory.ClearCertificateCache();
        var originalLoader = MongoClientFactory.CertLoader;

        var attempts = 0;
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=cert-cache-retry", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var realCert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        MongoClientFactory.CertLoader = (path, passphrase) =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n == 1)
            {
                throw new IOException("transient");
            }

            return realCert;
        };

        try
        {
            var method = typeof(MongoClientFactory).GetMethod(
                "GetOrLoadCertificate",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            // First call: loader throws; the Lazy should be evicted so the next call retries.
            var ex = Assert.Throws<TargetInvocationException>(
                () => method!.Invoke(null, ["/fake/retry.pfx", "pw"]));
            Assert.IsType<IOException>(ex.InnerException);

            // Second call: should succeed (loader returns realCert), proving the cache wasn't poisoned.
            var result = method!.Invoke(null, ["/fake/retry.pfx", "pw"]);
            Assert.Same(realCert, result);
            Assert.Equal(2, attempts);
        }
        finally
        {
            MongoClientFactory.CertLoader = originalLoader;
            MongoClientFactory.ClearCertificateCache();
        }
    }
}
