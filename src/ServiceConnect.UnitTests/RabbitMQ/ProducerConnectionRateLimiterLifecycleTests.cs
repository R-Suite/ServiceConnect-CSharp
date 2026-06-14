using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

[Collection(SerialConcurrencyCollection.Name)]
public sealed class ProducerConnectionRateLimiterLifecycleTests
{
    private static ProducerConnection CreateProducerConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        // Empty settings → defaults; PublisherAcknowledgements defaults to true so the
        // limiter path inside CreateConnectionAsync fires.
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());
        return new ProducerConnection(transport.Object, NullLogger.Instance);
    }

    private static (Mock<IConnection>, Mock<IChannel>) StubConnectionAndChannel()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        connection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        return (connection, channel);
    }

    private static FieldInfo LimiterField =>
        typeof(ProducerConnection).GetField("_publisherRateLimiter",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Field _publisherRateLimiter not found on ProducerConnection.");

    [Fact]
    public async Task ReconnectCycle_DisposesPriorLimiterAndInstallsFresh()
    {
        // Each CreateConnectionAsync allocates a ConcurrencyLimiter and hands it to
        // RabbitMQ.Client; the driver does not own user-supplied limiters. Without the
        // dispose lifecycle on _publisherRateLimiter, every reconnect leaks one limiter.
        var producer = CreateProducerConnection();

        var (connection, _) = StubConnectionAndChannel();
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(connection.Object);

        await producer.EnsureConnectedAsync(CancellationToken.None);

        var firstLimiter = (RateLimiter?)LimiterField.GetValue(producer);
        Assert.NotNull(firstLimiter);

        // Force a reconnect: MarkResetRequired flips the flag so the next
        // EnsureConnectedAsync drives a teardown + create cycle.
        producer.MarkResetRequired();
        await producer.EnsureConnectedAsync(CancellationToken.None);

        var secondLimiter = (RateLimiter?)LimiterField.GetValue(producer);
        Assert.NotNull(secondLimiter);
        Assert.NotSame(firstLimiter, secondLimiter);

        // The first limiter must be disposed: a stale limiter is the leak we are closing.
        // ConcurrencyLimiter.AttemptAcquire raises ObjectDisposedException after Dispose.
        Assert.Throws<ObjectDisposedException>(() => firstLimiter!.AttemptAcquire(0));
    }

    [Fact]
    public async Task CloseAsync_DisposesCurrentLimiterAndClearsField()
    {
        var producer = CreateProducerConnection();

        var (connection, _) = StubConnectionAndChannel();
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(connection.Object);

        await producer.EnsureConnectedAsync(CancellationToken.None);

        var liveLimiter = (RateLimiter?)LimiterField.GetValue(producer);
        Assert.NotNull(liveLimiter);

        await producer.CloseAsync(TimeSpan.FromSeconds(5));

        // After teardown the field is cleared so a fresh CreateConnectionAsync can install
        // a new limiter without observing a stale reference.
        Assert.Null(LimiterField.GetValue(producer));
        // And the live limiter at close time was disposed — not just nulled.
        Assert.Throws<ObjectDisposedException>(() => liveLimiter!.AttemptAcquire(0));
    }

    [Fact]
    public async Task RepeatedReconnects_KeepLimiterCountBoundedAtOne()
    {
        // Stress: many reconnects must not accumulate live limiters. We track each limiter
        // installed across cycles; at every step exactly one should be alive (the current
        // _publisherRateLimiter) and all priors should be Disposed.
        var producer = CreateProducerConnection();

        var (connection, _) = StubConnectionAndChannel();
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(connection.Object);

        await producer.EnsureConnectedAsync(CancellationToken.None);
        var observed = new List<RateLimiter>
        {
            (RateLimiter)LimiterField.GetValue(producer)!,
        };

        for (int i = 0; i < 8; i++)
        {
            producer.MarkResetRequired();
            await producer.EnsureConnectedAsync(CancellationToken.None);
            observed.Add((RateLimiter)LimiterField.GetValue(producer)!);
        }

        // Final state: 9 distinct limiters created (1 + 8), only the last one is live.
        Assert.Equal(9, observed.Distinct().Count());
        for (int i = 0; i < observed.Count - 1; i++)
        {
            Assert.Throws<ObjectDisposedException>(() => observed[i].AttemptAcquire(0));
        }
        // The current (last) limiter is still live.
        var currentLease = observed[^1].AttemptAcquire(0);
        Assert.NotNull(currentLease);
        currentLease.Dispose();

        await producer.CloseAsync(TimeSpan.FromSeconds(5));
        // After dispose, the last one is also gone.
        Assert.Throws<ObjectDisposedException>(() => observed[^1].AttemptAcquire(0));
    }
}
