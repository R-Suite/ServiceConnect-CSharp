using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that ConnectionFactoryBuilder.Build no longer emits the plaintext-non-loopback warning
/// (the warning was moved to the core layer; see ServiceConnectBuilderPlaintextWarningTests).
/// </summary>
public class ConnectionFactoryBuilderWarningTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("rabbit.example.com")]
    [InlineData("rabbitmq")]
    public void Build_WithSslDisabled_EmitsNoWarning(string host)
    {
        // The plaintext warning moved to the core layer (ServiceConnectBuilder.WarnIfPlaintextOnNonLoopbackHost
        // called from BusHostedService.StartAsync). The adapter no longer duplicates it.
        var transport = new TransportConfiguration { Host = host, SslEnabled = false };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        Assert.Empty(fakeLogger.Collector.GetSnapshot());
    }

    [Fact]
    public void Build_WithSslEnabled_EmitsNoWarning()
    {
        var transport = new TransportConfiguration
        {
            Host = "rabbit.example.com",
            SslEnabled = true,
            ServerName = "rabbit.example.com",
        };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        Assert.Empty(fakeLogger.Collector.GetSnapshot());
    }

    /// <summary>Placeholder type so FakeLogger has a category.</summary>
    public sealed class ConnectionFactoryBuilderTag { }
}
