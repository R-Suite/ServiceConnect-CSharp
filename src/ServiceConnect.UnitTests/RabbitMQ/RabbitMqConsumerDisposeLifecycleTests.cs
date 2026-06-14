using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMqClient = global::RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that DisposeAsync, when it times out waiting to acquire the dispose semaphore,
/// leaves the started flag set and does not null out any in-use setup channel.
/// The dispose semaphore (_disposeSemaphore) is independent of the startup semaphore
/// (_startupSemaphore), so a wedged startup cannot block container shutdown.
/// </summary>
public class RabbitMqConsumerDisposeLifecycleTests
{
    /// <summary>
    /// Returns a Consumer configured with ConsumerCount=1 and the supplied DisposeTimeout.
    /// The transport and queue mocks are wired with minimal defaults sufficient to construct
    /// a Consumer without triggering any real I/O.
    /// </summary>
    private static Consumer CreateConsumer(TimeSpan disposeTimeout, IServiceConnectConnection? connection = null)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.MaxRetries).Returns(3);
        transport.SetupGet(t => t.RetryDelay).Returns(1000);
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        var queue = new Mock<IQueueConfiguration>();
        queue.SetupGet(q => q.QueueName).Returns("q");
        queue.SetupGet(q => q.ErrorQueueName).Returns("err");
        queue.SetupGet(q => q.AuditQueueName).Returns("audit");
        queue.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);
        queue.SetupGet(q => q.AuditingEnabled).Returns(false);

        var bus = new Mock<IBusConfiguration>();
        bus.SetupGet(b => b.ConsumerCount).Returns(1);
        bus.SetupGet(b => b.DisposeTimeout).Returns(disposeTimeout);

        return new Consumer(transport.Object, queue.Object, bus.Object, NullLogger<Consumer>.Instance, connection);
    }

    /// <summary>
    /// Reads a private instance field via reflection. Throws if the field is not found.
    /// </summary>
    private static T? GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        return (T?)field.GetValue(target);
    }

    /// <summary>
    /// Sets a private instance field via reflection.
    /// </summary>
    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {target.GetType().Name}");
        field.SetValue(target, value);
    }

    /// <summary>
    /// Drains the dispose semaphore on the consumer so the next DisposeAsync WaitAsync call times out,
    /// simulating a concurrent DisposeAsync that already holds the permit.
    /// </summary>
    private static void DrainDisposeSemaphore(object consumer)
    {
        var semaphore = GetPrivateField<SemaphoreSlim>(consumer, "_disposeSemaphore")
            ?? throw new InvalidOperationException("_disposeSemaphore was null");
        // Take the one available permit so a subsequent WaitAsync with a short timeout returns false.
        semaphore.Wait(TimeSpan.Zero);
    }

    [Fact]
    public async Task DisposeAsync_when_lifecycle_wait_times_out_does_not_reset_started_flag()
    {
        // Arrange: construct a consumer with ConsumerCount=1 and a short DisposeTimeout.
        var connection = new Mock<IServiceConnectConnection>();
        var consumer = CreateConsumer(TimeSpan.FromMilliseconds(200), connection.Object);

        // Simulate that a concurrent DisposeAsync already holds the dispose semaphore,
        // and StartConsumingAsync has CAS'd _started to 1. The second DisposeAsync must
        // time out rather than hang indefinitely.
        SetPrivateField(consumer, "_started", 1);
        DrainDisposeSemaphore(consumer);

        // Act: DisposeAsync should time out acquiring the dispose semaphore.
        await consumer.DisposeAsync();

        // Assert: _started must remain 1. A subsequent StartConsumingAsync must throw
        // InvalidOperationException ("already consuming") instead of silently building
        // duplicate state.
        var started = GetPrivateField<int>(consumer, "_started");
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task DisposeAsync_when_lifecycle_wait_times_out_does_not_null_model_owned_by_wedged_start()
    {
        // Arrange: construct a consumer with a short DisposeTimeout.
        var connection = new Mock<IServiceConnectConnection>();
        var consumer = CreateConsumer(TimeSpan.FromMilliseconds(200), connection.Object);

        // Set up a sentinel channel to represent the setup channel that an in-flight
        // StartConsumingAsync has assigned to _model. Drain the dispose semaphore to
        // simulate a concurrent DisposeAsync already holding the permit; this causes
        // the next DisposeAsync.WaitAsync to time out and skip teardown.
        var sentinelChannel = new Mock<RabbitMqClient.IChannel>().Object;
        SetPrivateField(consumer, "_started", 1);
        SetPrivateField(consumer, "_model", sentinelChannel);
        DrainDisposeSemaphore(consumer);

        // Act: DisposeAsync should time out acquiring the dispose semaphore.
        await consumer.DisposeAsync();

        // Assert: _model must still be the sentinel. Nulling it here would tear down
        // a channel still in active use, producing opaque AlreadyClosed exceptions.
        var model = GetPrivateField<RabbitMqClient.IChannel>(consumer, "_model");
        Assert.Same(sentinelChannel, model);
    }
}
