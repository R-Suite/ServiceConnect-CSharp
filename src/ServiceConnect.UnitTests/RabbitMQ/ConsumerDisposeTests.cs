using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Unit-level guards on Consumer.DisposeAsync — covers field-state invariants that
/// must hold so a subsequent StartConsumingAsync does not reuse disposed resources
/// or accumulate stale per-cycle clients.
/// </summary>
public class ConsumerDisposeTests
{
    private static Consumer CreateConsumer(IServiceConnectConnection? connection = null)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(0);
        transport.SetupGet(t => t.RetryDelay).Returns(0);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.ConsumerCount).Returns(1);

        return new Consumer(transport.Object, queue.Object, bus.Object, NullLogger<Consumer>.Instance, connection);
    }

    private static void SetField<T>(Consumer consumer, string fieldName, T value)
    {
        typeof(Consumer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(consumer, value);
    }

    private static T GetField<T>(Consumer consumer, string fieldName)
    {
        return (T)typeof(Consumer)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer)!;
    }

    [Fact]
    public async Task DisposeAsync_WhenOwnsConnection_NullsConnectionField()
    {
        var consumer = CreateConsumer();
        var connectionMock = new Mock<IServiceConnectConnection>();
        connectionMock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        SetField(consumer, "_connection", connectionMock.Object);
        SetField(consumer, "_ownsConnection", true);

        await consumer.DisposeAsync();

        Assert.Null(GetField<IServiceConnectConnection?>(consumer, "_connection"));
        connectionMock.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_WhenConnectionIsCallerOwned_LeavesFieldIntact()
    {
        var connectionMock = new Mock<IServiceConnectConnection>();
        var consumer = CreateConsumer(connectionMock.Object);

        await consumer.DisposeAsync();

        // Caller-owned connection MUST NOT be disposed by Consumer.
        connectionMock.Verify(c => c.DisposeAsync(), Times.Never);
        // Caller-owned connection field must remain intact so the same instance is
        // reused across StartConsumingAsync calls.
        Assert.Same(connectionMock.Object, GetField<IServiceConnectConnection?>(consumer, "_connection"));
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllClientsAndClearsBag()
    {
        var consumer = CreateConsumer();
        var clients = GetField<ConcurrentBag<IAsyncDisposable>>(consumer, "_clients");

        var c1 = new Mock<IAsyncDisposable>();
        c1.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var c2 = new Mock<IAsyncDisposable>();
        c2.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        clients.Add(c1.Object);
        clients.Add(c2.Object);

        await consumer.DisposeAsync();

        // Each registered client is disposed exactly once.
        c1.Verify(c => c.DisposeAsync(), Times.Once);
        c2.Verify(c => c.DisposeAsync(), Times.Once);
        // Bag is empty after dispose so a subsequent StartConsumingAsync does not
        // accumulate stale entries.
        Assert.Empty(clients);
    }

    [Fact]
    public async Task DisposeAsync_AcrossMultipleCycles_LeavesBagBounded()
    {
        var consumer = CreateConsumer();
        var clients = GetField<ConcurrentBag<IAsyncDisposable>>(consumer, "_clients");

        for (int cycle = 0; cycle < 5; cycle++)
        {
            // Simulate a Start that registered 3 clients, then a Dispose.
            for (int i = 0; i < 3; i++)
            {
                var mock = new Mock<IAsyncDisposable>();
                mock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                clients.Add(mock.Object);
            }
            await consumer.DisposeAsync();
            Assert.Empty(clients);
        }
    }
}
