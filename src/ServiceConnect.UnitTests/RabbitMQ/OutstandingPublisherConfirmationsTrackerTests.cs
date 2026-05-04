using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="ProducerConnection"/> resolves a sane default permit limit for the
/// outstanding publisher-confirms rate limiter, and that operator overrides are respected.
/// Misconfigured values throw so they surface loudly rather than silently falling back.
/// </summary>
public class OutstandingPublisherConfirmationsTrackerTests
{
    [Fact]
    public void ResolveMaxOutstandingPublishConfirms_DefaultsTo256_WhenSettingUnset()
    {
        var transport = new TransportConfiguration();

        var permits = ProducerConnection.ResolveMaxOutstandingPublishConfirms(transport);

        Assert.Equal(256, permits);
    }

    [Fact]
    public void ResolveMaxOutstandingPublishConfirms_ReturnsExplicitValue_WhenSettingIsPositiveInt()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, 16);

        var permits = ProducerConnection.ResolveMaxOutstandingPublishConfirms(transport);

        Assert.Equal(16, permits);
    }

    [Fact]
    public void ResolveMaxOutstandingPublishConfirms_Throws_WhenSettingIsWrongType()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, "256");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProducerConnection.ResolveMaxOutstandingPublishConfirms(transport));

        Assert.Contains(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, ex.Message);
        Assert.Contains("must be an int", ex.Message);
    }

    [Fact]
    public void ResolveMaxOutstandingPublishConfirms_Throws_WhenSettingIsNonPositive()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, 0);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProducerConnection.ResolveMaxOutstandingPublishConfirms(transport));

        Assert.Contains(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, ex.Message);
        Assert.Contains("must be positive", ex.Message);
    }
}
