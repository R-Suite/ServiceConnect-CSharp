using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ConnectionFactoryBuilderWarningTests
{
    [Theory]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("10.0.0.5", true)]
    [InlineData("rabbit.example.com", true)]
    public void Build_WithSslDisabled_WarnsOnlyForNonLoopbackHosts(string host, bool warningExpected)
    {
        var transport = new TransportConfiguration { Host = host, SslEnabled = false };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        var records = fakeLogger.Collector.GetSnapshot();
        if (warningExpected)
        {
            Assert.Single(records);
            Assert.Equal(LogLevel.Warning, records[0].Level);
            Assert.Equal(RabbitMqClientLog.PlaintextOnNonLoopbackHostEventId, records[0].Id.Id);
            Assert.Contains(host, records[0].Message);
        }
        else
        {
            Assert.Empty(records);
        }
    }

    [Fact]
    public void Build_WithSslEnabled_DoesNotWarn_RegardlessOfHost()
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

    [Fact]
    public void Build_WithMixedClusterHostList_WarnsOnFirstNonLoopbackEntry()
    {
        var transport = new TransportConfiguration { Host = "localhost,rabbit-2", SslEnabled = false };
        var fakeLogger = new FakeLogger<ConnectionFactoryBuilderTag>();

        ConnectionFactoryBuilder.Build(transport, fakeLogger);

        var records = fakeLogger.Collector.GetSnapshot();
        Assert.Single(records);
        Assert.Equal(LogLevel.Warning, records[0].Level);
        Assert.Contains("rabbit-2", records[0].Message);
    }

    /// <summary>Placeholder type so FakeLogger has a category — the actual ILogger
    /// passed to ConnectionFactoryBuilder.Build is generic ILogger.</summary>
    public sealed class ConnectionFactoryBuilderTag { }
}
