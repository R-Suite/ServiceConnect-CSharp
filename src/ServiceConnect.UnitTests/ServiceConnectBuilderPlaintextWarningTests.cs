using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies the adapter-independent plaintext-against-non-loopback-host warning emitted by
/// <see cref="ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost"/>.
/// </summary>
public class ServiceConnectBuilderPlaintextWarningTests
{
    private static FakeLogger<PlaintextWarningTag> MakeLogger() => new();

    // TLS off + loopback variants — no warning expected
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void WarnIfPlaintext_SslOff_LoopbackHost_NoWarning(string host)
    {
        var transport = new TransportConfiguration { Host = host, SslEnabled = false };
        var logger = MakeLogger();

        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    // TLS off + non-loopback — warning expected
    [Theory]
    [InlineData("rabbitmq")]
    [InlineData("10.0.0.5")]
    [InlineData("rabbit.example.com")]
    [InlineData("192.168.1.10")]
    public void WarnIfPlaintext_SslOff_NonLoopbackHost_WarningFires(string host)
    {
        var transport = new TransportConfiguration { Host = host, SslEnabled = false };
        var logger = MakeLogger();

        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        var records = logger.Collector.GetSnapshot();
        Assert.Single(records);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Equal(ServiceConnectLog.PlaintextOnNonLoopbackHostEventId, records[0].Id.Id);
        Assert.Contains(host, records[0].Message);
    }

    [Fact]
    public void WarnIfPlaintext_SslOn_NonLoopbackHost_NoWarning()
    {
        var transport = new TransportConfiguration
        {
            Host = "rabbitmq",
            SslEnabled = true,
            ServerName = "rabbitmq",
        };
        var logger = MakeLogger();

        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void WarnIfPlaintext_SuppressPlaintextWarning_NonLoopbackHost_NoWarning()
    {
        // Docker Compose deployments where plaintext is intentional can set
        // SuppressPlaintextWarning=true to silence the noise.
        var transport = new TransportConfiguration
        {
            Host = "rabbitmq",
            SslEnabled = false,
            SuppressPlaintextWarning = true,
        };
        var logger = MakeLogger();

        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    [Fact]
    public void WarnIfPlaintext_SslOff_ClusterList_WarnsOnFirstNonLoopback()
    {
        // Mixed host list: first entry is loopback, second is non-loopback.
        // Exactly one warning, containing the non-loopback entry.
        var transport = new TransportConfiguration { Host = "localhost,rabbitmq", SslEnabled = false };
        var logger = MakeLogger();

        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        var records = logger.Collector.GetSnapshot();
        Assert.Single(records);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Contains("rabbitmq", records[0].Message);
    }

    [Fact]
    public void WarnIfPlaintext_SslOff_SuppressPlaintextWarning_ClusterList_NoWarning()
    {
        var transport = new TransportConfiguration
        {
            Host = "localhost,rabbitmq",
            SslEnabled = false,
            SuppressPlaintextWarning = true,
        };
        var logger = MakeLogger();

        ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost(transport, logger);

        Assert.Empty(logger.Collector.GetSnapshot());
    }

    /// <summary>Placeholder type for the FakeLogger category.</summary>
    public sealed class PlaintextWarningTag { }
}
