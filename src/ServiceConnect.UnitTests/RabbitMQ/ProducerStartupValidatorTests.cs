using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ProducerStartupValidatorTests
{
    private static (Mock<ITransportConfiguration> transport, Mock<IQueueConfiguration> queue, Mock<IBusConfiguration> bus) BuildMocks(
        Dictionary<string, object>? extraSettings = null)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        var settings = new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        };
        if (extraSettings != null)
        {
            foreach (var kvp in extraSettings)
            {
                settings[kvp.Key] = kvp.Value;
            }
        }
        transport.SetupGet(t => t.ClientSettings).Returns(settings);

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        return (transport, queue, bus);
    }

    [Fact]
    public void Constructor_DefaultPublisherAcksIsTrue()
    {
        // Default config — no PublisherAcknowledgements override, no PublishTimeout override.
        // Construction must not throw under defaults; the absence of an exception proves the
        // default is "acks on, timeout valid" rather than "acks off, timeout-not-meaningful".
        var (transport, queue, bus) = BuildMocks();
        _ = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    [Fact]
    public void Constructor_RejectsAcksFalseWithNonzeroPublishTimeout()
    {
        var (transport, queue, bus) = BuildMocks(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublisherAcknowledgements] = false,
            [RabbitMQSettingKeys.PublishTimeout] = TimeSpan.FromSeconds(5),
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance));
        Assert.Contains("PublisherAcknowledgements", ex.Message);
        Assert.Contains("PublishTimeout", ex.Message);
    }

    [Fact]
    public void Constructor_AllowsAcksFalseWithInfiniteTimeout()
    {
        // Caller explicitly opts out of acks AND sets PublishTimeout to Infinite (so they're
        // not relying on broker confirms for timeout enforcement). Construction succeeds.
        var (transport, queue, bus) = BuildMocks(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublisherAcknowledgements] = false,
            [RabbitMQSettingKeys.PublishTimeout] = Timeout.InfiniteTimeSpan,
        });

        _ = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }

    [Fact]
    public void Constructor_AllowsAcksFalseWithZeroTimeout()
    {
        // The validator's error message advertises TimeSpan.Zero as a valid remediation
        // alongside Timeout.InfiniteTimeSpan. Pin both escape hatches with their own tests
        // so a future tightening of the guard (e.g. >= TimeSpan.Zero) cannot silently
        // contradict the documented advice.
        var (transport, queue, bus) = BuildMocks(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.PublisherAcknowledgements] = false,
            [RabbitMQSettingKeys.PublishTimeout] = TimeSpan.Zero,
        });

        _ = new Producer(transport.Object, queue.Object, bus.Object, NullLogger<Producer>.Instance);
    }
}
