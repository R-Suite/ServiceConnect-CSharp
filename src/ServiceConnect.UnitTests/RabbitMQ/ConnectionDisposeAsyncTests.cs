using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public class ConnectionDisposeAsyncTests
{
    private static Connection CreateConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        return new Connection(transport.Object, "q", NullLogger.Instance);
    }

    [Fact(Timeout = 5_000)]
    public async Task DisposeAsync_LockHeldElsewhere_StillCompletesWithinDisposeTimeout()
    {
        var connection = CreateConnection();

        var timeoutField = typeof(Connection).GetField("_disposeLockTimeout",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        timeoutField.SetValue(connection, TimeSpan.FromMilliseconds(100));

        var lockField = typeof(Connection).GetField("_connectionLock",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var sema = (SemaphoreSlim)lockField.GetValue(connection)!;
        await sema.WaitAsync();

        var disposeStart = DateTime.UtcNow;
        await connection.DisposeAsync();
        var elapsed = DateTime.UtcNow - disposeStart;

        Assert.True(elapsed < TimeSpan.FromSeconds(3),
            $"DisposeAsync should respect _disposeLockTimeout but took {elapsed}");
    }
}
