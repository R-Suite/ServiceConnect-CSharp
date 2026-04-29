using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class OutboundHeaderBuilderPriorityTests
{
    private static (OutboundHeaderBuilder builder, List<(LogLevel Level, string Message, Exception? Exception)> logs) CreateBuilder()
    {
        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("q");

        var captured = new List<(LogLevel, string, Exception?)>();
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
            .Callback(new InvocationAction(invocation =>
            {
                var level = (LogLevel)invocation.Arguments[0];
                var state = invocation.Arguments[2];
                var ex = (Exception?)invocation.Arguments[3];
                var formatter = invocation.Arguments[4];
                var message = (string)formatter.GetType()
                    .GetMethod("Invoke")!
                    .Invoke(formatter, [state, ex])!;
                captured.Add((level, message, ex));
            }));

        var builder = new OutboundHeaderBuilder(
            busConfig.Object,
            queueConfig.Object,
            new FakeTimeProvider(),
            logger.Object);
        return (builder, captured);
    }

    [Fact]
    public void Priority_ValidByte_StampsAndDoesNotLog()
    {
        var (builder, logs) = CreateBuilder();
        var headers = builder.BuildHeaders(typeof(string), null, "q", "Publish");
        headers[HeaderKeys.Priority] = (byte)5;

        var props = builder.BuildBasicProperties(headers);

        Assert.True(props.IsPriorityPresent());
        Assert.Equal((byte)5, props.Priority);
        Assert.Empty(logs);
    }

    [Fact]
    public void Priority_OutOfRangeInt_LogsValueAndType_ContinuesWithoutPriority()
    {
        var (builder, logs) = CreateBuilder();
        var headers = builder.BuildHeaders(typeof(string), null, "q", "Publish");
        headers[HeaderKeys.Priority] = 300;

        var props = builder.BuildBasicProperties(headers);

        Assert.False(props.IsPriorityPresent());
        var error = Assert.Single(logs, l => l.Level == LogLevel.Error);
        Assert.Contains("300", error.Message);
        Assert.Contains("Int32", error.Message);
    }

    [Fact]
    public void Priority_NonNumericString_LogsValueAndType_ContinuesWithoutPriority()
    {
        var (builder, logs) = CreateBuilder();
        var headers = builder.BuildHeaders(typeof(string), null, "q", "Publish");
        headers[HeaderKeys.Priority] = "abc";

        var props = builder.BuildBasicProperties(headers);

        Assert.False(props.IsPriorityPresent());
        var error = Assert.Single(logs, l => l.Level == LogLevel.Error);
        Assert.Contains("abc", error.Message);
        Assert.Contains("System.String", error.Message);
    }
}
