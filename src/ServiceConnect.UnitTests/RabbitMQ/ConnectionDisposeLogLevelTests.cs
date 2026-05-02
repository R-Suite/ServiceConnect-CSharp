using Microsoft.Extensions.Logging;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConnectionDisposeLogLevelTests
{
    private static (Connection connection, List<(LogLevel Level, string Message)> logs) Build(Func<IConnection> connectionFactory)
    {
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

        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>(0));

        var connection = new Connection(transport.Object, "test-queue", logger.Object)
        {
            CreateConnectionForTests = (_, _, _, _) => Task.FromResult(connectionFactory()),
        };

        return (connection, captured);
    }

    [Fact]
    public async Task DisposeAsync_TeardownThrows_LogsAtWarning()
    {
        var mockConn = new Mock<IConnection>();
        mockConn.SetupGet(c => c.IsOpen).Returns(true);
        // CloseAsync() with no args is an extension method; mock the underlying overload it delegates to.
        mockConn.Setup(c => c.CloseAsync(
                It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated teardown failure"));
        mockConn.Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IChannel>());

        var (connection, logs) = Build(() => mockConn.Object);

        // Establish the connection by creating a channel.
        await connection.CreateChannelAsync(default);

        // Now dispose; CloseAsync throws → catch logs at Warning post-fix.
        await connection.DisposeAsync();

        // Pre-fix: log entry at Debug.
        // Post-fix: log entry at Warning containing "Error closing connection".
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("Error closing connection"));
        Assert.DoesNotContain(logs, l => l.Level == LogLevel.Debug && l.Message.Contains("Error closing connection"));
    }
}
