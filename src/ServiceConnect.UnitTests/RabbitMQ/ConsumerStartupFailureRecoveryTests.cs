using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

public sealed class ConsumerStartupFailureRecoveryTests
{
    [Fact]
    public async Task StartConsumingAsync_FailureMidStartup_ResetsStartedFlagAndAllowsRetry()
    {
        // Stage a Consumer whose first StartConsumingAsync fails at topology declaration.
        // Without the fix, _started stays at 1 and the second StartConsumingAsync throws
        // "already consuming" — even though nothing is consuming. With the fix, _started
        // is reset on failure, so the second StartConsumingAsync proceeds.
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        transport.SetupGet(t => t.MaxRetries).Returns(0);
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(1000);
        transport.SetupGet(t => t.RetryDelay).Returns(0);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("startup-failure-q");
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("startup-failure-q.errors");
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.ConsumerCount).Returns(1);

        var attempt = 0;
        var failingConnection = new Mock<IServiceConnectConnection>();
        failingConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempt++;
                var channel = new Mock<IChannel>();
                channel.SetupGet(c => c.IsOpen).Returns(true);
                channel.Setup(c => c.CloseAsync(
                        It.IsAny<ushort>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                if (attempt == 1)
                {
                    // First attempt: fail at topology declaration so _started is set but nothing runs.
                    channel.Setup(c => c.ExchangeDeclareAsync(
                            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(),
                            It.IsAny<CancellationToken>()))
                        .ThrowsAsync(new InvalidOperationException("simulated topology failure"));
                }
                else
                {
                    // Second attempt: stub everything so StartConsumingAsync completes end-to-end.
                    // This proves _started was reset on the first failure.
                    channel.Setup(c => c.ExchangeDeclareAsync(
                            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(),
                            It.IsAny<CancellationToken>()))
                        .Returns(Task.CompletedTask);
                    channel.Setup(c => c.QueueDeclareAsync(
                            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
                            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<bool>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new QueueDeclareOk("startup-failure-q", 0, 0));
                    channel.Setup(c => c.QueueBindAsync(
                            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                            It.IsAny<IDictionary<string, object?>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                        .Returns(Task.CompletedTask);
                    channel.Setup(c => c.BasicQosAsync(
                            It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                        .Returns(Task.CompletedTask);
                    channel.Setup(c => c.BasicConsumeAsync(
                            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(),
                            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IDictionary<string, object?>?>(),
                            It.IsAny<IAsyncBasicConsumer>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync("test-tag");
                }
                return Task.FromResult(channel.Object);
            });
        // The host's PrepareAsync calls CreateChannelAsync(CreateChannelOptions?, CT) for the
        // publish channel; route that to the same per-attempt logic.
        failingConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(() => failingConnection.Object.CreateChannelAsync(default));
        failingConnection.SetupGet(c => c.UnderlyingConnection).Returns((global::RabbitMQ.Client.IConnection?)null);

        var consumer = new Consumer(
            transport.Object, queueConfig.Object, busConfig.Object,
            NullLogger<Consumer>.Instance, failingConnection.Object);

        // First attempt: topology declare throws. _started should reset on the way out.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            consumer.StartConsumingAsync("startup-failure-q", ["TestMessage"], (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));

        // Second attempt: now the topology succeeds; this would have failed pre-fix
        // with "already consuming".
        await consumer.StartConsumingAsync("startup-failure-q", ["TestMessage"], (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true }));

        await consumer.DisposeAsync();
    }

    [Fact]
    public async Task StartConsumingAsync_FailureWithOwnedConnection_DisposesOwnedConnectionAndResetsState()
    {
        // When the consumer is constructed without a connection, StartConsumingAsync allocates
        // one and sets _ownsConnection=true. If startup then fails, the catch block must dispose
        // the owned connection too — leaving the caller in the same state as construction so
        // they can retry without leaking the connection.
        //
        // The Connection class is constructed lazily (no real network call until CreateChannelAsync),
        // so we pre-inject a fake IServiceConnectConnection via reflection: _ownsConnection=true and
        // _connection pointing at a mock that fails CreateChannelAsync and records whether DisposeAsync
        // was called. This avoids real network timeouts while still exercising the cleanup path.
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("nonexistent-host-for-test");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        transport.SetupGet(t => t.MaxRetries).Returns(0);
        transport.SetupGet(t => t.GracefulShutdownTimeoutMilliseconds).Returns(1000);
        transport.SetupGet(t => t.RetryDelay).Returns(0);

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("startup-failure-q");
        queueConfig.SetupGet(q => q.ErrorQueueName).Returns("startup-failure-q.errors");
        queueConfig.SetupGet(q => q.AuditingEnabled).Returns(false);
        queueConfig.SetupGet(q => q.PurgeQueueOnStartup).Returns(false);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.ConsumerCount).Returns(1);

        // Construct the consumer with no connection so _ownsConnection=true is set in the ctor.
        var consumer = new Consumer(
            transport.Object, queueConfig.Object, busConfig.Object,
            NullLogger<Consumer>.Instance, connection: null);

        // Build a fake IServiceConnectConnection whose CreateChannelAsync always throws.
        // We track whether DisposeAsync is called via a flag.
        var connectionDisposed = false;
        var fakeConnection = new Mock<IServiceConnectConnection>();
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated connection failure"));
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated connection failure"));
        fakeConnection
            .Setup(c => c.DisposeAsync())
            .Returns(() =>
            {
                connectionDisposed = true;
                return ValueTask.CompletedTask;
            });

        // Pre-inject the fake connection via reflection so the consumer treats it as
        // the owned connection it "created" during a previous partial start.
        var connectionField = typeof(Consumer).GetField(
            "_connection", BindingFlags.Instance | BindingFlags.NonPublic);
        var ownsField = typeof(Consumer).GetField(
            "_ownsConnection", BindingFlags.Instance | BindingFlags.NonPublic);

        connectionField!.SetValue(consumer, fakeConnection.Object);
        ownsField!.SetValue(consumer, true);

        // StartConsumingAsync must fail (connection throws on CreateChannelAsync).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            consumer.StartConsumingAsync("startup-failure-q", ["TestMessage"], (_, _, _, _) => Task.FromResult(new ConsumeEventResult { Success = true })));

        // The catch block must have disposed the owned connection and cleared the fields.
        Assert.True(connectionDisposed, "_connection.DisposeAsync was not called during failure recovery");
        Assert.Null(connectionField.GetValue(consumer));
        Assert.False((bool)ownsField.GetValue(consumer)!);
    }
}
