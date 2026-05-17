using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Verifies that <see cref="ProducerConnection.EnsureConnectedAsync"/> tears down a
/// stale connection when the channel was closed by the broker (an unsolicited
/// Channel.Close from queue deletion, policy violation, or mirror failover) without
/// any in-flight publisher having called <c>MarkResetRequired</c>.
///
/// Without the bare-close teardown trigger, the second EnsureConnectedAsync would
/// observe <c>_resetRequired == 0 &amp;&amp; !IsHealthy()</c>, skip teardown, and fall
/// through to <c>CreateConnectionAsync</c>, which overwrites <c>_connection</c>
/// without disposing the prior reference — leaking one AMQP connection per broker-
/// side channel close until GC finalises it.
/// </summary>
[Collection(SerialConcurrencyCollection.Name)]
public sealed class ProducerConnectionStaleChannelTests
{
    private static ProducerConnection CreateProducerConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)1,
            [RabbitMQSettingKeys.RetrySeconds] = (ushort)0,
        });

        return new ProducerConnection(transport.Object, NullLogger.Instance);
    }

    [Fact]
    public async Task EnsureConnectedAsync_WhenChannelClosedWithoutResetMark_DisposesPriorConnectionBeforeReplacement()
    {
        // Arrange: a ProducerConnection that has completed one successful create. The first
        // mock IConnection's IsOpen toggles via a backing field so the test can flip the
        // channel's IsOpen to false (simulating a broker-driven Channel.Close) and observe
        // that the next EnsureConnectedAsync disposes the prior IConnection before assigning
        // a replacement.
        var producerConnection = CreateProducerConnection();

        var firstChannelOpen = true;
        var firstChannel = new Mock<IChannel>();
        firstChannel.SetupGet(c => c.IsOpen).Returns(() => firstChannelOpen);

        var firstConnectionDisposeCount = 0;
        var firstConnection = new Mock<IConnection>();
        // IsOpen=false on teardown skips the broker CloseAsync path (which is an extension
        // method on IConnection that Moq cannot intercept) and falls straight through to
        // Dispose(), which is what the leak-avoidance invariant turns on.
        firstConnection.SetupGet(c => c.IsOpen).Returns(false);
        firstConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstChannel.Object);
        firstConnection
            .Setup(c => c.Dispose())
            .Callback(() => Interlocked.Increment(ref firstConnectionDisposeCount));

        var secondChannel = new Mock<IChannel>();
        secondChannel.SetupGet(c => c.IsOpen).Returns(true);

        var secondConnection = new Mock<IConnection>();
        secondConnection.SetupGet(c => c.IsOpen).Returns(true);
        secondConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondChannel.Object);

        var createCallCount = 0;
        // Verifies the teardown order invariant: on the second create call, the prior
        // connection must already have had Dispose() invoked. Without the bare-close
        // teardown trigger, this check would fail because CreateConnectionAsync would
        // overwrite _connection while the first reference still held an undisposed handle.
        var firstConnectionDisposeCountAtSecondCreate = -1;
        producerConnection.CreateConnectionForTests = (_, _, _, _) =>
        {
            var call = Interlocked.Increment(ref createCallCount);
            if (call == 1)
            {
                return Task.FromResult(firstConnection.Object);
            }

            firstConnectionDisposeCountAtSecondCreate = Volatile.Read(ref firstConnectionDisposeCount);
            return Task.FromResult(secondConnection.Object);
        };

        // Act: initial create.
        await producerConnection.EnsureConnectedAsync(CancellationToken.None);
        Assert.Equal(1, createCallCount);
        Assert.True(producerConnection.IsHealthy());

        // Simulate broker-driven Channel.Close: the channel reports closed but no publisher
        // has called MarkResetRequired. _resetRequired remains 0.
        firstChannelOpen = false;
        Assert.False(producerConnection.IsHealthy());
        Assert.False(producerConnection.ResetRequiredForTests);

        // Second call must tear down the stale connection before creating the replacement.
        await producerConnection.EnsureConnectedAsync(CancellationToken.None);

        // Assert: a second create happened, AND at the moment the second create ran the
        // first connection had already been disposed exactly once.
        Assert.Equal(2, createCallCount);
        Assert.Equal(1, firstConnectionDisposeCountAtSecondCreate);
        Assert.Equal(1, firstConnectionDisposeCount);
    }
}
