using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

public class ConnectionTests
{
    private static Connection CreateConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        return new Connection(transport.Object, "test-queue", NullLogger<Connection>.Instance);
    }

    private static void SetField<T>(Connection connection, string fieldName, T value)
    {
        typeof(Connection)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(connection, value);
    }

    private static TField GetField<TField>(Connection connection, string fieldName)
    {
        return (TField)typeof(Connection)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;
    }

    [Fact]
    public async Task CreateChannelAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var connection = CreateConnection();
        var underlying = new Mock<IConnection>();
        underlying.SetupGet(c => c.IsOpen).Returns(true);
        underlying.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        SetField(connection, "_connection", underlying.Object);

        await connection.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connection.CreateChannelAsync());
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledTwice_DoesNotThrowAndClosesConnectionOnce()
    {
        var connection = CreateConnection();
        var underlying = new Mock<IConnection>();
        underlying.SetupGet(c => c.IsOpen).Returns(true);
        underlying.Setup(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        SetField(connection, "_connection", underlying.Object);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        underlying.Verify(c => c.CloseAsync(It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        underlying.Verify(c => c.Dispose(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DisposesConnectionLockSemaphore()
    {
        var connection = CreateConnection();

        await connection.DisposeAsync();

        var semaphore = GetField<SemaphoreSlim>(connection, "_connectionLock");
        Assert.Throws<ObjectDisposedException>(() => semaphore.Wait(0));
    }
}
