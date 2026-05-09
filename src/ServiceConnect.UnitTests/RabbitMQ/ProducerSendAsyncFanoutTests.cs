using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using RabbitMQ.Client;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces;
using ServiceConnect.Interfaces.Configuration;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

/// <summary>
/// Tests for the fan-out partial-failure matrix of
/// <see cref="Producer.SendAsync(Type, ReadOnlyMemory{byte}, IReadOnlyDictionary{string,string}?, CancellationToken)"/>.
///
/// Tests 2 and 3 are intentionally RED until Task B.2 switches the loop to
/// continue-on-failure with AggregateException collection.  Tests 1 and 4
/// exercise behaviour that already holds under the current fail-fast loop.
/// </summary>
public sealed class ProducerSendAsyncFanoutTests
{
    // Shared body and headers used by every test.
    private static readonly ReadOnlyMemory<byte> Body = new byte[] { 0xDE, 0xAD, 0xBE };
    private static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Builds a <see cref="Producer"/> wired to a mock channel whose
    /// <c>BasicPublishAsync</c> delegates to <paramref name="publishHandler"/>,
    /// with a queue mapping of <c>TestMessage → ["q1", "q2", "q3"]</c>.
    /// Returns both the producer and the mock channel so callers can verify
    /// per-routing-key invocations afterward.
    /// </summary>
    private static (Producer Producer, Mock<IChannel> Channel) BuildProducerWithChannel(
        Func<string, string, bool, BasicProperties, ReadOnlyMemory<byte>, CancellationToken, ValueTask> publishHandler)
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        // RetryCount = 0 prevents the retry loop from delaying or wrapping the thrown exception
        // in an extra AggregateException layer.  The tests care about the exceptions thrown by
        // the fan-out loop itself, not by Retry.DoAsync's wrapping behaviour.
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>
        {
            [RabbitMQSettingKeys.RetryCount] = (ushort)0,
        });

        var queueConfig = new Mock<IQueueConfiguration>();
        queueConfig.SetupGet(q => q.QueueName).Returns("source-q");
        IReadOnlyList<string> endpoints = ["q1", "q2", "q3"];
        queueConfig.Setup(q => q.TryGetQueueMapping(typeof(TestMessage), out endpoints!)).Returns(true);

        var busConfig = new Mock<IBusConfiguration>();
        busConfig.SetupGet(b => b.IncludeMachineNameInHeaders).Returns(false);

        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel
            .Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string ex, string rk, bool m, BasicProperties bp, ReadOnlyMemory<byte> body, CancellationToken ct)
                => publishHandler(ex, rk, m, bp, body, ct));
        channel
            .Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>>(), false, false,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var producer = new Producer(
            transport.Object,
            queueConfig.Object,
            busConfig.Object,
            NullLogger<Producer>.Instance,
            new FakeTimeProvider());

        var fakeConnection = new Mock<IConnection>();
        fakeConnection.SetupGet(c => c.IsOpen).Returns(true);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channel.Object);
        producer.CreateConnectionForTests = (_, _, _, _) => Task.FromResult(fakeConnection.Object);

        return (producer, channel);
    }

    // -------------------------------------------------------------------------
    // Test 1 — all endpoints succeed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_AllEndpointsSucceed_NoException()
    {
        int callCount = 0;
        var (producer, _) = BuildProducerWithChannel((_, _, _, _, _, _) =>
        {
            callCount++;
            return ValueTask.CompletedTask;
        });

        // No exception should be thrown.
        await producer.SendAsync(typeof(TestMessage), Body, Headers, CancellationToken.None);

        // All three endpoints must have been published to.
        Assert.Equal(3, callCount);
    }

    // -------------------------------------------------------------------------
    // Test 2 — single endpoint fails: expect AggregateException, others attempted
    // (RED until Task B.2 — current loop rethrows directly and never reaches q2/q3)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_SingleEndpointFails_AggregateExceptionWithOneInner_OtherEndpointsAttempted()
    {
        // TimeoutException is non-retriable (IsRetriablePublishException returns false), so
        // Retry.DoAsync rethrows it directly rather than wrapping it in AggregateException.
        // This keeps the AggregateException that the test asserts against flat and unambiguous.
        var (producer, channel) = BuildProducerWithChannel((_, rk, _, _, _, _) =>
        {
            if (rk == "q1")
            {
                throw new TimeoutException("q1: broker ack timed out");
            }
            return ValueTask.CompletedTask;
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => producer.SendAsync(typeof(TestMessage), Body, Headers, CancellationToken.None));

        Assert.Single(ex.InnerExceptions);
        Assert.IsType<TimeoutException>(ex.InnerExceptions[0]);

        // q2 and q3 must each have been attempted despite q1 failing.
        channel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), "q2", It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), "q3", It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // -------------------------------------------------------------------------
    // Test 3 — all endpoints fail: expect AggregateException with all inners
    // (RED until Task B.2 — current loop rethrows on the first failure)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_AllEndpointsFail_AggregateExceptionWithAllInners()
    {
        // TimeoutException is non-retriable (IsRetriablePublishException returns false) so
        // Retry.DoAsync propagates it immediately.  Using distinct messages lets us verify
        // that each endpoint contributed exactly one inner exception.
        var (producer, _) = BuildProducerWithChannel((_, rk, _, _, _, _) =>
        {
            throw new TimeoutException($"endpoint {rk}: broker ack timed out");
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => producer.SendAsync(typeof(TestMessage), Body, Headers, CancellationToken.None));

        Assert.Equal(3, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, e => Assert.IsType<TimeoutException>(e));

        // Each of the three endpoint names must appear in exactly one inner exception message.
        var messages = ex.InnerExceptions.Cast<TimeoutException>().Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("q1"));
        Assert.Contains(messages, m => m.Contains("q2"));
        Assert.Contains(messages, m => m.Contains("q3"));
    }

    // -------------------------------------------------------------------------
    // Test 4 — cancellation mid-loop: OperationCanceledException propagated directly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_CancellationMidLoop_ThrowsOperationCanceledDirectly()
    {
        using var cts = new CancellationTokenSource();

        var (producer, channel) = BuildProducerWithChannel((_, rk, _, _, _, _) =>
        {
            if (rk == "q1")
            {
                // Cancel the token and then throw OperationCanceledException so the loop
                // sees a genuine cancellation on q1. Both the current fail-fast loop and the
                // B.2 continue-on-failure loop must rethrow this directly rather than wrapping
                // it in AggregateException.
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }

            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => producer.SendAsync(typeof(TestMessage), Body, Headers, cts.Token));

        // The loop must have aborted: q2 and q3 should never have been published.
        channel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), "q2", It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        channel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), "q3", It.IsAny<bool>(),
                It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -------------------------------------------------------------------------
    // Test 5 — prior failures + later cancellation aggregates both
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SendAsync_PriorEndpointFailure_ThenCancellation_AggregatesBoth()
    {
        // q1 fails (non-retriable TimeoutException — accumulated). q2 succeeds. q3 raises
        // OCE. Caller must see an AggregateException whose inner exceptions include both
        // q1's TimeoutException and q3's OperationCanceledException.
        using var cts = new CancellationTokenSource();
        var (producer, _) = BuildProducerWithChannel((_, rk, _, _, _, _) =>
        {
            switch (rk)
            {
                case "q1":
                    throw new TimeoutException("q1: broker ack timed out");
                case "q2":
                    return ValueTask.CompletedTask;
                case "q3":
                    cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask; // unreachable
                default:
                    return ValueTask.CompletedTask;
            }
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => producer.SendAsync(typeof(TestMessage), Body, Headers, cts.Token));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains(ex.InnerExceptions, e => e is TimeoutException);
        Assert.Contains(ex.InnerExceptions, e => e is OperationCanceledException);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class TestMessage { }
}
