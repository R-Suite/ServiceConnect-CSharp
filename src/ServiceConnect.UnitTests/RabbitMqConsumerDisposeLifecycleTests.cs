using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMqClient = global::RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests;

/// <summary>
/// Verifies that DisposeAsync, when it times out waiting for an in-flight StartConsumingAsync,
/// leaves the started flag set and does not null out the setup channel that the wedged start
/// still owns.
/// </summary>
public class RabbitMqConsumerDisposeLifecycleTests
{
    private static Mock<ITransportConfiguration> MakeTransportCfg()
    {
        var cfg = new Mock<ITransportConfiguration>();
        cfg.SetupGet(c => c.MaxRetries).Returns(3);
        cfg.SetupGet(c => c.RetryDelay).Returns(1000);
        cfg.SetupGet(c => c.ClientSettings).Returns(new Dictionary<string, object>());
        return cfg;
    }

    private static Mock<IQueueConfiguration> MakeQueueCfg()
    {
        var cfg = new Mock<IQueueConfiguration>();
        cfg.SetupGet(c => c.QueueName).Returns("q");
        cfg.SetupGet(c => c.ErrorQueueName).Returns("err");
        cfg.SetupGet(c => c.AuditQueueName).Returns("audit");
        cfg.SetupGet(c => c.PurgeQueueOnStartup).Returns(false);
        cfg.SetupGet(c => c.AuditingEnabled).Returns(false);
        return cfg;
    }

    /// <summary>
    /// Returns a bus configuration mock with a short DisposeTimeout so tests complete quickly
    /// when the lifecycle semaphore is artificially drained.
    /// </summary>
    private static Mock<IBusConfiguration> MakeBusCfg(TimeSpan disposeTimeout)
    {
        var cfg = new Mock<IBusConfiguration>();
        cfg.SetupGet(c => c.ConsumerCount).Returns(1);
        cfg.SetupGet(c => c.DisposeTimeout).Returns(disposeTimeout);
        return cfg;
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
    /// Drains the lifecycle semaphore on the consumer so the next WaitAsync call times out.
    /// </summary>
    private static void DrainLifecycleSemaphore(object consumer)
    {
        var semaphore = GetPrivateField<SemaphoreSlim>(consumer, "_lifecycleSemaphore")
            ?? throw new InvalidOperationException("_lifecycleSemaphore was null");
        // WaitAsync(0) returns false if already at 0; we need to take the one available permit.
        semaphore.Wait(TimeSpan.Zero);
    }

    [Fact]
    public async Task DisposeAsync_when_lifecycle_wait_times_out_does_not_reset_started_flag()
    {
        // Arrange: construct a consumer with ConsumerCount=1 and a short DisposeTimeout.
        var connection = new Mock<IServiceConnectConnection>();
        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg(TimeSpan.FromMilliseconds(100)).Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        // Simulate that StartConsumingAsync has CAS'd _started to 1 and is mid-setup,
        // holding the semaphore.
        SetPrivateField(consumer, "_started", 1);
        DrainLifecycleSemaphore(consumer);

        // Act: DisposeAsync should time out acquiring the lifecycle semaphore.
        await consumer.DisposeAsync();

        // Assert: _started must remain 1. A subsequent StartConsumingAsync must throw
        // InvalidOperationException ("already consuming") instead of CAS-ing to 1 and
        // then deadlocking on the semaphore the wedged start still holds.
        var started = GetPrivateField<int>(consumer, "_started");
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task DisposeAsync_when_lifecycle_wait_times_out_does_not_null_model_owned_by_wedged_start()
    {
        // Arrange: construct a consumer with a short DisposeTimeout.
        var connection = new Mock<IServiceConnectConnection>();
        var consumer = new Consumer(
            MakeTransportCfg().Object,
            MakeQueueCfg().Object,
            MakeBusCfg(TimeSpan.FromMilliseconds(100)).Object,
            NullLogger<Consumer>.Instance,
            connection.Object);

        // Set up a sentinel channel to represent the setup channel that an in-flight
        // StartConsumingAsync has assigned to _model.
        var sentinelChannel = new Mock<RabbitMqClient.IChannel>().Object;
        SetPrivateField(consumer, "_started", 1);
        SetPrivateField(consumer, "_model", sentinelChannel);
        DrainLifecycleSemaphore(consumer);

        // Act: DisposeAsync should time out acquiring the lifecycle semaphore.
        await consumer.DisposeAsync();

        // Assert: _model must still be the sentinel. Nulling it here would tear down
        // the setup channel from under the running start path, causing AlreadyClosed
        // exceptions out of topology declares.
        var model = GetPrivateField<RabbitMqClient.IChannel>(consumer, "_model");
        Assert.Same(sentinelChannel, model);
    }
}
