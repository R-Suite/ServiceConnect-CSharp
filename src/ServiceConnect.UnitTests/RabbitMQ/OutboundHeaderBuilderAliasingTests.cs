using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Locks in the zero-copy aliasing invariant: BuildBasicProperties assigns the input
/// messageHeaders dictionary directly to BasicProperties.Headers — no copy.
///
/// Maintenance hazard: this test only proves the immediate aliasing. The load-bearing
/// safety guarantee lives elsewhere — Producer.cs callers must not mutate messageHeaders
/// while a publish using the returned BasicProperties is in flight. SendAsync(Type)
/// already mutates between fan-out iterations; that's safe ONLY because publisher-confirms
/// gate the prior await PublishWithTimeoutAsync on the broker ack. See the aliasing-safety
/// comment in OutboundHeaderBuilder.BuildBasicProperties for the binding contract and the
/// publisher-confirms dependency. This test catches silent re-introduction of a defensive
/// copy; it does NOT catch new post-BuildBasicProperties mutation sites.
/// </summary>
public sealed class OutboundHeaderBuilderAliasingTests
{
    [Fact]
    public void BuildBasicProperties_AssignsHeadersDirectly_WithoutCopy()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");

        var builder = new OutboundHeaderBuilder(
            busConfig.Object, queueConfig.Object, new FakeTimeProvider(), NullLogger.Instance);

        var messageHeaders = builder.BuildHeaders(typeof(string), null, "framework-q", "Publish");

        var basicProperties = builder.BuildBasicProperties(messageHeaders);

        Assert.Same(messageHeaders, basicProperties.Headers);
    }
}
