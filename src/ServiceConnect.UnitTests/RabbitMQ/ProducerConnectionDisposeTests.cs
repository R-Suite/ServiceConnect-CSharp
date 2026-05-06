using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceConnect.Client.RabbitMQ;
using ServiceConnect.Interfaces.Configuration;
using ServiceConnect.UnitTests;
using Xunit;

namespace ServiceConnect.UnitTests.RabbitMQ;

[Collection(SerialConcurrencyCollection.Name)]
public sealed class ProducerConnectionDisposeTests
{
    private static ProducerConnection CreateProducerConnection()
    {
        var transport = new Mock<ITransportConfiguration>();
        transport.SetupGet(t => t.Host).Returns("localhost");
        transport.SetupGet(t => t.ClientSettings).Returns(new Dictionary<string, object>());

        return new ProducerConnection(transport.Object, NullLogger.Instance);
    }

    [Fact]
    public async Task ReconnectAsync_CancellationTokenCancelled_DisposeReturnsWithinBoundedTime()
    {
        // Arrange: construct a ProducerConnection, acquire its _connectionSemaphore in a
        // background task and hold it for longer than any reasonable test timeout, then
        // cancel the token quickly and assert ReconnectAsync returns within ~2s (well under
        // the 30s ceiling).
        var producerConnection = CreateProducerConnection();

        // Install a no-op ReconnectForTests hook so EnsureConnectedAsync is not called after
        // DisposeConnectionAsync (that would attempt a real AMQP connection).
        producerConnection.ReconnectForTests = _ => Task.CompletedTask;

        var semaphoreField = typeof(ProducerConnection).GetField(
            "_connectionSemaphore",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var semaphore = (SemaphoreSlim)semaphoreField!.GetValue(producerConnection)!;

        using var holderRelease = new ManualResetEventSlim(false);
        var holderTask = Task.Run(async () =>
        {
            await semaphore.WaitAsync().ConfigureAwait(false);
            try { holderRelease.Wait(); }
            finally { semaphore.Release(); }
        });

        // Allow the holder task a moment to acquire the semaphore.
        await Task.Delay(50);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var sw = Stopwatch.StartNew();
        // ReconnectAsync routes through DisposeConnectionAsync which honours both the
        // 30s ceiling and the cancellation token rather than blocking indefinitely.
        await producerConnection.ReconnectAsync(new InvalidOperationException("test"), cts.Token);
        sw.Stop();

        holderRelease.Set();
        await holderTask;

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"DisposeConnectionAsync took {sw.Elapsed} — should have returned within ~50ms+best-effort-teardown after cancellation, well under 2s.");
    }

    [Fact]
    public async Task DisposeDuringCreate_DoesNotOrphanConnection()
    {
        // Set up: stage a CreateConnectionForTests that takes long enough for Close to start
        // and time out its semaphore wait. After the create returns, _disposed is already 1
        // and the just-created connection should be torn down rather than assigned.
        var producerConnection = CreateProducerConnection();

        // Track whether the just-created connection's Dispose / DisposeAsync gets called.
        var disposeCount = 0;
        var fakeConnection = new Mock<global::RabbitMQ.Client.IConnection>();
        // IsOpen=false avoids the need to mock CloseAsync's full overload set; we only need
        // to verify Dispose() runs against the just-built instance.
        fakeConnection.SetupGet(c => c.IsOpen).Returns(false);
        fakeConnection.Setup(c => c.Dispose()).Callback(() => Interlocked.Increment(ref disposeCount));
        // Also need to mock CreateChannelAsync because CreateConnectionAsync calls it after the connection is built.
        var fakeChannel = new Mock<global::RabbitMQ.Client.IChannel>();
        fakeChannel.SetupGet(c => c.IsOpen).Returns(false);
        fakeConnection
            .Setup(c => c.CreateChannelAsync(It.IsAny<global::RabbitMQ.Client.CreateChannelOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeChannel.Object);

        var createReleased = new TaskCompletionSource();
        var createInvoked = new TaskCompletionSource();
        producerConnection.CreateConnectionForTests = async (_, _, _, _) =>
        {
            createInvoked.TrySetResult();
            await createReleased.Task; // hold here until the test releases
            return fakeConnection.Object;
        };

        // Begin a connection create on a worker task. EnsureConnectedAsync acquires the
        // semaphore and calls into CreateConnectionForTests, which blocks until createReleased.
        var ensureTask = producerConnection.EnsureConnectedAsync(CancellationToken.None);
        await createInvoked.Task;

        // Now drive Close with a tight timeout — it cannot acquire the semaphore (the create
        // holds it via the Retry loop's awaitable wait). Close sets _disposed and forces teardown
        // (which is a no-op since _connection/_model are still null).
        var closeTask = producerConnection.CloseAsync(TimeSpan.FromMilliseconds(50));
        await closeTask;

        // Allow the create to complete. The post-assign disposed check should detect _disposed
        // and tear down the just-built fakeConnection rather than orphaning it.
        createReleased.TrySetResult();

        // ensureTask may complete or throw; both are acceptable post-dispose. Wait for it.
        try { await ensureTask; }
        catch (ObjectDisposedException) { /* expected: post-assign check throws this */ }
        catch (Exception) { /* other races acceptable as long as fakeConnection.Dispose was called */ }

        Assert.Equal(1, disposeCount);
    }
}
