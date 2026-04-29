using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class OutboundHeaderBuilderReservedHeaderWarningTests
{
    private static (OutboundHeaderBuilder builder, List<(LogLevel Level, string Message)> logs) CreateBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");

        var captured = new List<(LogLevel, string)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception?>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var formatter = (Delegate)invocation.Arguments[4];
                var message = (string)formatter.DynamicInvoke(invocation.Arguments[2], invocation.Arguments[3])!;
                captured.Add((level, message));
            }));

        return (
            new OutboundHeaderBuilder(busConfig.Object, queueConfig.Object, new FakeTimeProvider(), logger.Object),
            captured);
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesReservedHeader_FrameworkValueWins_AndWarns()
    {
        var (builder, logs) = CreateBuilder();
        var caller = new Dictionary<string, string>
        {
            [HeaderKeys.DestinationAddress] = "user-supplied-dest",
            [HeaderKeys.TypeName] = "user.spoof.type",
            ["X-Custom"] = "ok",
        };

        var result = builder.BuildHeaders(typeof(string), caller, "framework-q", "Publish");

        // Framework wins for reserved keys.
        Assert.Equal("framework-q", result[HeaderKeys.DestinationAddress]);
        Assert.Equal(typeof(string).FullName, result[HeaderKeys.TypeName]);
        // Non-reserved header flows through.
        Assert.Equal("ok", result["X-Custom"]);

        // One warning per overwritten reserved key, each containing the key name.
        var warnings = logs.Where(l => l.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains(HeaderKeys.DestinationAddress));
        Assert.Contains(warnings, w => w.Message.Contains(HeaderKeys.TypeName));
    }

    [Fact]
    public void BuildHeaders_CallerSuppliesMessageId_PreservedNoWarning()
    {
        // MessageId is deliberately NOT in the overwrite set — caller-supplied (Bus's
        // authoritative stamp) is preserved by the !ContainsKey check.
        var (builder, logs) = CreateBuilder();
        var bus = new Dictionary<string, string>
        {
            [HeaderKeys.MessageId] = "bus-stamped-id",
        };

        var result = builder.BuildHeaders(typeof(string), bus, "q", "Publish");

        Assert.Equal("bus-stamped-id", result[HeaderKeys.MessageId]);
        Assert.DoesNotContain(logs, l => l.Level == LogLevel.Warning);
    }
}
