using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class OutboundHeaderBuilderHopCounterTests
{
    private static OutboundHeaderBuilder NewBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("test-q");

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        return new OutboundHeaderBuilder(busConfig.Object, queueConfig.Object, new FakeTimeProvider(), logger.Object);
    }

    [Fact]
    public void BuildHeaders_FrameworkHopsSet_StampsOnOutput()
    {
        var builder = NewBuilder();
        var result = builder.BuildHeaders(
            type: typeof(string),
            headers: null,
            queueName: "dest-q",
            messageType: "TestMessage",
            routingSlipHopsCompleted: 4);

        Assert.Equal("4", result[HeaderKeys.RoutingSlipHopsCompleted]);
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesHopHeader_FrameworkValueWins()
    {
        var builder = NewBuilder();
        var callerHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HeaderKeys.RoutingSlipHopsCompleted] = "0",
        };

        var result = builder.BuildHeaders(
            type: typeof(string),
            headers: callerHeaders,
            queueName: "dest-q",
            messageType: "TestMessage",
            routingSlipHopsCompleted: 4);

        Assert.Equal("4", result[HeaderKeys.RoutingSlipHopsCompleted]);
    }

    [Fact]
    public void BuildHeaders_NoFrameworkHops_DoesNotStamp()
    {
        var builder = NewBuilder();
        var result = builder.BuildHeaders(
            type: typeof(string),
            headers: null,
            queueName: "dest-q",
            messageType: "TestMessage",
            routingSlipHopsCompleted: null);

        Assert.False(result.ContainsKey(HeaderKeys.RoutingSlipHopsCompleted));
    }
}
