using System.Threading.RateLimiting;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="ProducerConnection"/> bounds the in-flight publisher-confirms
/// tracker that RabbitMQ.Client v7.2.1 leaves unbounded by default.
/// </summary>
public class OutstandingPublisherConfirmationsTrackerTests
{
    [Fact]
    public void ConcurrencyLimiter_DefaultsTo256Permits_WhenSettingUnset()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.PublisherAcknowledgements, true);

        var limiter = ProducerConnection.TestAccess.BuildPermitLimiter(transport);

        Assert.NotNull(limiter);
        var concurrency = Assert.IsType<ConcurrencyLimiter>(limiter);
        Assert.Equal(256, concurrency.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public void ConcurrencyLimiter_RespectsExplicitMaxOutstandingPublishConfirms()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.PublisherAcknowledgements, true);
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, 16);

        var limiter = ProducerConnection.TestAccess.BuildPermitLimiter(transport);

        var concurrency = Assert.IsType<ConcurrencyLimiter>(limiter);
        Assert.Equal(16, concurrency.GetStatistics()!.CurrentAvailablePermits);
    }
}
