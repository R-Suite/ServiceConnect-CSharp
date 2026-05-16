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

    [Theory]
    [InlineData("256")]
    [InlineData(256L)]
    [InlineData((short)256)]
    public void ResolveMaxOutstandingPublishConfirms_Coerces_WhenSettingIsConvertibleNumericOrString(object raw)
    {
        // Configuration sources (IConfiguration binders, env-var binders, JSON) routinely produce
        // long, string, or other numeric types instead of int. Resolver matches the codebase
        // convention (Convert.ToInt32) so binding succeeds without forcing the caller to cast.
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, raw);

        var permits = ProducerConnection.ResolveMaxOutstandingPublishConfirms(transport);

        Assert.Equal(256, permits);
    }

    [Fact]
    public void ResolveMaxOutstandingPublishConfirms_Throws_WhenSettingIsUnconvertible()
    {
        var transport = new TransportConfiguration();
        transport.SetClientSetting(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, "not-a-number");

        var ex = Assert.Throws<InvalidOperationException>(
            () => ProducerConnection.ResolveMaxOutstandingPublishConfirms(transport));

        Assert.Contains(RabbitMQSettingKeys.MaxOutstandingPublishConfirms, ex.Message);
        Assert.Contains("convertible to Int32", ex.Message);
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
