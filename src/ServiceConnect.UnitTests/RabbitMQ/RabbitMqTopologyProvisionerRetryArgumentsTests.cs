using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class RabbitMqTopologyProvisionerRetryArgumentsTests
{
    [Fact]
    public async Task ConfigureRetryTopologyAsync_CallerSuppliesFrameworkArgs_FrameworkValuesWin_AndDebugLogged()
    {
        IDictionary<string, object?>? capturedArgs = null;
        var channel = new Mock<IChannel>();
        channel
            .Setup(c => c.ExchangeDeclareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(), false, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel
            .Setup(c => c.QueueBindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object?>>(), false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel
            .Setup(c => c.QueueDeclareAsync(
                It.Is<string>(s => s.EndsWith(".Retries", StringComparison.Ordinal)),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>>(),
                false, false, It.IsAny<CancellationToken>()))
            .Callback<string, bool, bool, bool, IDictionary<string, object?>, bool, bool, CancellationToken>(
                (_, _, _, _, args, _, _, _) => capturedArgs = new Dictionary<string, object?>(args!, StringComparer.Ordinal))
            .ReturnsAsync(new QueueDeclareOk("q", 0, 0));

        var captured = new List<(LogLevel Level, string Message)>();
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

        var caller = new Dictionary<string, object?>
        {
            [RabbitMqQueueNaming.XDeadLetterExchangeArgument] = "user-supplied-dlx",
            [RabbitMqQueueNaming.XMessageTtlArgument] = 999,
            ["x-max-length"] = 1000,
        };

        var provisioner = new RabbitMqTopologyProvisioner(logger.Object);
        await provisioner.ConfigureRetryTopologyAsync(
            channel.Object,
            queueName: "main-q",
            durable: true,
            autoDelete: false,
            retryDelayMs: 5000,
            retryQueueArguments: caller,
            isInitialSetup: true);

        Assert.NotNull(capturedArgs);
        Assert.Equal("main-q.Retries.DeadLetter", capturedArgs![RabbitMqQueueNaming.XDeadLetterExchangeArgument]);
        Assert.Equal(5000, capturedArgs[RabbitMqQueueNaming.XMessageTtlArgument]);
        Assert.Equal(1000, capturedArgs["x-max-length"]); // non-conflicting key flows through

        var debugLogs = captured.Where(l => l.Level == LogLevel.Debug).ToList();
        Assert.Equal(2, debugLogs.Count);
        Assert.Contains(debugLogs, l => l.Message.Contains(RabbitMqQueueNaming.XDeadLetterExchangeArgument));
        Assert.Contains(debugLogs, l => l.Message.Contains(RabbitMqQueueNaming.XMessageTtlArgument));
    }
}
