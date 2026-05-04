using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Locks in the Group F item 4 invariant: BuildBasicProperties does NOT copy
/// messageHeaders — it assigns the source dictionary directly to BasicProperties.Headers.
///
/// Maintenance hazard: this test only proves the immediate aliasing. If a future change
/// adds post-BuildBasicProperties mutation of messageHeaders (in Producer.cs or any
/// future caller), the alias becomes unsafe and the test won't catch it. The spec's
/// no-mutation invariant on the Producer.cs publish/send paths is the load-bearing
/// guarantee; this test is a regression guard against silently re-introducing the copy.
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

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var builder = new OutboundHeaderBuilder(
            busConfig.Object, queueConfig.Object, new FakeTimeProvider(), logger.Object);

        var messageHeaders = builder.BuildHeaders(typeof(string), null, "framework-q", "Publish");

        var basicProperties = builder.BuildBasicProperties(messageHeaders);

        Assert.Same(messageHeaders, basicProperties.Headers);
    }
}
